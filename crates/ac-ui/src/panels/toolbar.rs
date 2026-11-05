//! The toolbar: the eighteen shortcut slots (two rows of nine, the top row
//! numbered), the main-panel buttons with their open-state faces, the
//! combat-mode indicators, the use and examine buttons, the ammunition
//! counter, the main-backpack button that takes dropped items, and the
//! selected stack's split quantity (slider and typed entry).
//!
//! The session owns the shortcut bar; the toolbar shows its copy
//! ([`ShortcutStore`]), replaced whenever the session's bar in the
//! [`ItemsView`] changes. Moving, adding and removing a shortcut change the
//! copy at once and go out as [`PanelOutput::AddShortcut`] /
//! [`PanelOutput::RemoveShortcut`] for the session, whose bar then comes
//! back the same. Everything about the objects comes from the view at each
//! update.

use std::sync::Arc;

use ac_core::combat::CombatMode;
use ac_core::items::shortcuts::{
    ShortcutDropSource, ShortcutMutation, plan_refused_shortcut_drop, plan_shortcut_drop,
};
use ac_core::items::{
    ClientObject, ItemType, PublicWeenieFlags, ShortcutEntry, ShortcutStore, StackSplitQuantity,
    ToolbarAmmo,
};
use ac_runtime::inventory::{ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;

use crate::draw::Color;
use crate::event::{DragAcceptance, ItemDragPayload, ItemDragSource};
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::inventory::{PLAYER_PACK_BASE_ICON, SelectionEcho, capacity_fill};
use crate::panels::items::{ItemsView, emit, offer_target};
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::tree::ElementId;
use crate::widgets::item_slot::PACK_DRAG_ACCEPT_SPRITE;
use crate::widgets::{Button, Field, ItemList, ItemSlot, Scrollbar, ShortcutDigits};

/// The toolbar's layout.
pub const LAYOUT_ID: u32 = 0x2100_0016;
/// The eighteen shortcut slots: the numbered top row, then the bottom row.
pub const SLOT_IDS: [u32; 18] = [
    0x1000_01A7,
    0x1000_01A8,
    0x1000_01A9,
    0x1000_01AA,
    0x1000_01AB,
    0x1000_01AC,
    0x1000_01AD,
    0x1000_01AE,
    0x1000_01AF,
    0x1000_06B7,
    0x1000_06B8,
    0x1000_06B9,
    0x1000_06BA,
    0x1000_06BB,
    0x1000_06BC,
    0x1000_06BD,
    0x1000_06BE,
    0x1000_06BF,
];
/// The combat-mode indicators: peace, melee, missile, magic.
pub const COMBAT_INDICATOR_IDS: [u32; 4] = [0x1000_0192, 0x1000_0193, 0x1000_0194, 0x1000_0195];
/// The property naming a button's main panel.
pub const PANEL_ID_ATTRIBUTE: u32 = 0x1000_0029;
/// The use button.
pub const USE_BUTTON_ID: u32 = 0x1000_019D;
/// The examine button.
pub const EXAMINE_BUTTON_ID: u32 = 0x1000_01A5;
/// The missile indicator that shows the ammunition count.
pub const AMMO_INDICATOR_ID: u32 = 0x1000_0194;
/// The main-backpack (inventory) button.
pub const INVENTORY_BUTTON_ID: u32 = 0x1000_01B1;
/// The main-panel buttons.
pub const PANEL_BUTTON_IDS: [u32; 7] = [
    0x1000_0197,
    0x1000_0198,
    0x1000_0199,
    0x1000_055A,
    0x1000_019A,
    0x1000_019B,
    INVENTORY_BUTTON_ID,
];
/// The selected stack's typed split quantity.
pub const STACK_SIZE_ENTRY_ID: u32 = 0x1000_01A3;
/// The selected stack's split slider.
pub const STACK_SIZE_SLIDER_ID: u32 = 0x1000_01A4;
/// The overlay a shortcut slot shows while a drop would be taken.
pub const SHORTCUT_DRAG_ACCEPT_SPRITE: u32 = 0x0600_11FA;
/// Numbered slots (keys 1-9).
pub const NUMBERED_SLOTS: usize = 9;

/// What a toolbar key asks for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ToolbarKey {
    /// Use the shortcut in slot `n` (0-based).
    Use(usize),
    /// Select the shortcut's object in slot `n`.
    Select(usize),
    /// Make a shortcut to the selected object.
    CreateShortcut,
}

/// The slot a retail quick-slot key names: use keys 1-18 (`use_key`,
/// 1-based) and select keys 1-9 (`select_key`).
#[must_use]
pub fn quick_slot(use_key: Option<usize>, select_key: Option<usize>) -> Option<ToolbarKey> {
    match (use_key, select_key) {
        (Some(n @ 1..=18), _) => Some(ToolbarKey::Use(n - 1)),
        (None, Some(n @ 1..=9)) => Some(ToolbarKey::Select(n - 1)),
        _ => None,
    }
}

/// The toolbar's controller.
#[derive(Debug)]
pub struct ToolbarController {
    slots: [Option<ElementId>; 18],
    combat_indicators: [Option<ElementId>; 4],
    panel_buttons: Vec<(u32, ElementId)>,
    inventory_button: Option<ElementId>,
    use_button: Option<ElementId>,
    examine_button: Option<ElementId>,
    ammo_indicator: Option<ElementId>,
    store: ShortcutStore,
    /// The session's bar as last seen: when it changes the bar here is
    /// replaced by it.
    session_bar: Option<[Option<ShortcutEntry>; 18]>,
    digits: Option<Arc<ShortcutDigits>>,
    selection: SelectionEcho,
    target_source: Option<u32>,
    target_mode: bool,
    player: u32,
    queue: Vec<(ElementId, ElementMessage)>,
    /// Items from outside the bar dropped on a slot, judged at the next
    /// update against the objects: (slot, payload).
    fresh_drops: Vec<(i32, ItemDragPayload)>,
    ammo_object: u32,
}

/// Whether a toolbar slot takes the drag: an item itself, or a shortcut
/// moved along the bar; any other alias (a salvage, vendor or house
/// window cell) is refused.
fn takes_shortcut_drop(p: &ItemDragPayload) -> bool {
    !p.source_kind.is_alias() || p.source_kind == ItemDragSource::ShortcutBar
}

/// `item` may have a shortcut: it is not stuck in place, not a creature
/// and not in the open vendor's stock, unless it is a player.
fn is_shortcut_eligible(o: &ClientObject, view: &ItemsView<'_>) -> bool {
    let flags = o.flags();
    let is_player = o.object_id() == view.player || flags.contains(PublicWeenieFlags::PLAYER);
    if flags.contains(PublicWeenieFlags::STUCK) && !is_player {
        return false;
    }
    if o.item_type.intersects(ItemType::CREATURE) && !is_player {
        return false;
    }
    o.container_id == 0 || o.container_id != view.open_vendor
}

fn cell_of(ui: &UiRoot, list: Option<ElementId>) -> Option<ElementId> {
    list.and_then(|l| ui.widget::<ItemList>(l))
        .and_then(|l| l.item(0))
}

fn apply(ui: &mut UiRoot, store: &mut ShortcutStore, plan: &[ShortcutMutation]) {
    for m in plan {
        match *m {
            ShortcutMutation::Remove(slot) => {
                emit(ui, PanelOutput::RemoveShortcut(slot));
                store.remove(slot);
            }
            ShortcutMutation::Add(entry) => {
                emit(ui, PanelOutput::AddShortcut(entry));
                store.set(entry);
            }
        }
    }
}

impl ToolbarController {
    /// Bind to an imported toolbar.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        digits: Option<Arc<ShortcutDigits>>,
        ammo_font: Option<Arc<crate::UiFont>>,
    ) -> Self {
        let mut slots = [None; 18];
        for (i, id) in SLOT_IDS.iter().enumerate() {
            let Some(list) = layout
                .find(*id)
                .filter(|l| ui.widget::<ItemList>(*l).is_some())
            else {
                continue;
            };
            if let Some(c) = cell_of(ui, Some(list))
                && let Some(s) = ui.widget_mut::<ItemSlot>(c)
            {
                s.slot_index = i as i32;
                s.source_kind = ItemDragSource::ShortcutBar;
                s.drag_accept_sprite = SHORTCUT_DRAG_ACCEPT_SPRITE;
            }
            slots[i] = Some(list);
        }
        let mut combat_indicators = [None; 4];
        for (slot, id) in combat_indicators.iter_mut().zip(COMBAT_INDICATOR_IDS) {
            *slot = layout.find(id);
            if let Some(b) = *slot
                && let Some(w) = ui.widget_mut::<Button>(b)
            {
                w.listeners.click = true;
            }
        }
        let ammo_indicator = layout
            .find(AMMO_INDICATOR_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some());
        if let Some(b) = ammo_indicator
            && let Some(w) = ui.widget_mut::<Button>(b)
        {
            // The count draws in the button's own font; the one handed in
            // is for a layout that names none.
            if w.label_font.is_none() {
                w.label_font = ammo_font;
            }
            w.label_color = Color::WHITE;
        }
        let mut panel_buttons = Vec::new();
        for id in PANEL_BUTTON_IDS {
            if let Some(b) = layout.find(id)
                && let Some(panel) = ui
                    .widget::<Button>(b)
                    .and_then(|w| w.enum_attribute(PANEL_ID_ATTRIBUTE))
            {
                panel_buttons.push((panel, b));
            }
        }
        let inventory_button = layout
            .find(INVENTORY_BUTTON_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some());
        if let Some(b) = inventory_button
            && let Some(w) = ui.widget_mut::<Button>(b)
        {
            w.item_drag_accept_sprite = PACK_DRAG_ACCEPT_SPRITE;
            w.listeners.item_drop = true;
            w.on_item_drag_over = Some(Box::new(|p: &ItemDragPayload| {
                if p.obj_id != 0 && !p.source_kind.is_alias() {
                    DragAcceptance::Accept
                } else {
                    DragAcceptance::None
                }
            }));
        }
        let button = |ui: &mut UiRoot, id| {
            let b = layout
                .find(id)
                .filter(|b| ui.widget::<Button>(*b).is_some());
            if let Some(b) = b
                && let Some(w) = ui.widget_mut::<Button>(b)
            {
                w.listeners.click = true;
            }
            b
        };
        let use_button = button(ui, USE_BUTTON_ID);
        let examine_button = button(ui, EXAMINE_BUTTON_ID);
        let c = Self {
            slots,
            combat_indicators,
            panel_buttons,
            inventory_button,
            use_button,
            examine_button,
            ammo_indicator,
            store: ShortcutStore::new(),
            session_bar: None,
            digits,
            selection: SelectionEcho::default(),
            target_source: None,
            target_mode: false,
            player: 0,
            queue: Vec::new(),
            fresh_drops: Vec::new(),
            ammo_object: 0,
        };
        c.show_combat_mode(ui, CombatMode::NON_COMBAT);
        c
    }

    /// The shortcut bar.
    #[must_use]
    pub fn shortcuts(&self) -> &ShortcutStore {
        &self.store
    }

    /// Load the shortcut bar (the character's description).
    pub fn load_shortcuts(&mut self, entries: impl IntoIterator<Item = ShortcutEntry>) {
        self.store.load(entries);
    }

    /// The item list of slot `index`.
    #[must_use]
    pub fn slot_list(&self, index: usize) -> Option<ElementId> {
        self.slots.get(index).copied().flatten()
    }

    /// The ammunition object the counter shows (0: hidden).
    #[must_use]
    pub fn ammo_object(&self) -> u32 {
        self.ammo_object
    }

    /// The panel buttons: (main panel id, button).
    #[must_use]
    pub fn panel_buttons(&self) -> &[(u32, ElementId)] {
        &self.panel_buttons
    }

    /// Enable the panel buttons whose panels exist (a button of an absent
    /// panel ghosts).
    pub fn bind_panel_buttons(&self, ui: &mut UiRoot, is_available: impl Fn(u32) -> bool) {
        for &(panel, b) in &self.panel_buttons {
            let available = is_available(panel);
            if let Some(w) = ui.widget_mut::<Button>(b) {
                w.listeners.click = available;
            }
            ui.set_enabled(b, available);
        }
    }

    /// Show panel `panel_id`'s button open (highlighted) or closed.
    pub fn set_panel_open(&self, ui: &mut UiRoot, panel_id: u32, open: bool) {
        if let Some(&(_, b)) = self.panel_buttons.iter().find(|(p, _)| *p == panel_id) {
            if !ui.element(b).is_some_and(|e| e.enabled) {
                return;
            }
            ui.set_retail_state(
                b,
                if open {
                    states::HIGHLIGHT
                } else {
                    states::NORMAL
                },
            );
        }
    }

    fn show_combat_mode(&self, ui: &mut UiRoot, mode: CombatMode) {
        let show = [
            mode == CombatMode::NON_COMBAT,
            mode == CombatMode::MELEE,
            mode == CombatMode::MISSILE,
            mode == CombatMode::MAGIC,
        ];
        for (e, on) in self.combat_indicators.iter().zip(show) {
            if let Some(e) = *e {
                ui.set_visible(e, on);
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

    fn use_item(ui: &mut UiRoot, id: u32) {
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(id)))),
        );
    }

    /// A quick-slot key: in a target mode the slot's object is the target
    /// (an empty slot cancels the mode); else the object is used or
    /// selected. False for an empty slot outside a target mode.
    pub fn use_shortcut(&mut self, ui: &mut UiRoot, slot: usize, use_it: bool) -> bool {
        let Some(id) = (slot < self.slots.len()).then(|| self.store.get(slot as i32)) else {
            return false;
        };
        if self.target_mode {
            if id != 0 {
                offer_target(ui, ObjectRef::Guid(id));
            } else {
                emit(ui, PanelOutput::CancelTargetMode);
            }
            return true;
        }
        if id == 0 {
            return false;
        }
        if use_it {
            Self::use_item(ui, id);
        } else {
            self.select(ui, id);
        }
        true
    }

    /// A toolbar key ([`quick_slot`]); the selected object is the one a
    /// new shortcut points at.
    pub fn handle_key(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, key: ToolbarKey) -> bool {
        match key {
            ToolbarKey::Use(n) => self.use_shortcut(ui, n, true),
            ToolbarKey::Select(n) => self.use_shortcut(ui, n, false),
            ToolbarKey::CreateShortcut => {
                self.create_shortcut_to_item(ui, view, view.selected.unwrap_or(0));
                true
            }
        }
    }

    /// Put a shortcut to `item` in the first empty slot: an owned item (or
    /// the player) that is not stuck, not a creature and not already on
    /// the bar.
    pub fn create_shortcut_to_item(
        &mut self,
        ui: &mut UiRoot,
        view: &ItemsView<'_>,
        item: u32,
    ) -> bool {
        let Some(o) = view.get(item).filter(|_| item != 0) else {
            return false;
        };
        if !is_shortcut_eligible(o, view) {
            return false;
        }
        let is_player = item == view.player || o.flags().contains(PublicWeenieFlags::PLAYER);
        if !is_player && !view.objects.is_owned_by_object(item, view.player) {
            return false;
        }
        if (0..SLOT_IDS.len() as i32).any(|s| self.store.get(s) == item) {
            return false;
        }
        let Some(empty) = (0..SLOT_IDS.len() as i32).find(|s| self.store.is_empty(*s)) else {
            return false;
        };
        let entry = ShortcutEntry {
            index: empty,
            object_id: item,
            spell_id: 0,
        };
        emit(ui, PanelOutput::AddShortcut(entry));
        self.store.set(entry);
        true
    }

    fn dragged_entry(p: &ItemDragPayload) -> ShortcutEntry {
        p.shortcut.unwrap_or(ShortcutEntry {
            index: p.source_slot,
            object_id: p.obj_id,
            spell_id: 0,
        })
    }

    /// A shortcut dragged from another slot, dropped on `target_slot`.
    fn drop_shortcut_on_slot(&mut self, ui: &mut UiRoot, target_slot: i32, p: &ItemDragPayload) {
        let plan = plan_shortcut_drop(
            &self.store.snapshot(),
            ShortcutDropSource::ShortcutAlias,
            p.source_slot,
            target_slot,
            Self::dragged_entry(p),
        );
        apply(ui, &mut self.store, &plan);
    }

    /// An item from outside the bar dropped on `target_slot`. The slot's
    /// shortcut makes way (to the first empty slot on its right) whatever
    /// the item is. An item that may not have a shortcut gets none; one of
    /// the player's gets it at once; anything else (on the ground, in an
    /// outside container) is picked up into the preferred pack first, and
    /// the session gives it the shortcut once that request goes out.
    fn drop_fresh_item_on_slot(
        &mut self,
        ui: &mut UiRoot,
        target_slot: i32,
        p: &ItemDragPayload,
        view: &ItemsView<'_>,
    ) {
        let item = p.obj_id;
        if item == 0 {
            return;
        }
        let Some(o) = view.get(item).filter(|o| is_shortcut_eligible(o, view)) else {
            let plan = plan_refused_shortcut_drop(&self.store.snapshot(), target_slot, item);
            apply(ui, &mut self.store, &plan);
            return;
        };
        let plan = plan_shortcut_drop(
            &self.store.snapshot(),
            ShortcutDropSource::FreshItem,
            p.source_slot,
            target_slot,
            Self::dragged_entry(p),
        );
        let is_player = item == view.player || o.flags().contains(PublicWeenieFlags::PLAYER);
        if is_player || view.objects.is_owned_by_object(item, view.player) {
            apply(ui, &mut self.store, &plan);
            return;
        }
        for m in plan {
            match m {
                // The item's shortcut elsewhere and its new one are the
                // session's to change once the pickup has gone out.
                ShortcutMutation::Remove(slot) if slot != target_slot => {}
                ShortcutMutation::Add(e) if e.object_id == item && e.index == target_slot => {
                    emit(
                        ui,
                        PanelOutput::Session(SessionCommand::Item(ItemCommand::PickUpToShortcut {
                            item: ObjectRef::Guid(item),
                            slot: target_slot,
                        })),
                    );
                }
                m => apply(ui, &mut self.store, &[m]),
            }
        }
    }

    /// Resolve what was queued against `view`, then show it.
    pub fn update(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        self.player = view.player;
        // The backpack stands for the player (the cursor's "found" form).
        if let Some(b) = self.inventory_button
            && let Some(w) = ui.widget_mut::<crate::widgets::Button>(b)
        {
            w.represented_object = view.player;
        }
        self.target_source = view.target_source;
        self.target_mode = view.target_mode;
        self.selection.sync(view.selected);
        if let Some(bar) = view.shortcuts {
            // The session owns the bar; the changes made here go to it and
            // come back, and any other change (a new description) shows.
            let snapshot = bar.snapshot();
            if self.session_bar != Some(snapshot) {
                self.session_bar = Some(snapshot);
                self.store.load(bar.items());
            }
        }
        for (element, message) in std::mem::take(&mut self.queue) {
            if let ElementMessage::ItemDropped(p) = message
                && Some(element) == self.inventory_button
            {
                self.inventory_button_drop(ui, &p, view);
            }
        }
        for (slot, p) in std::mem::take(&mut self.fresh_drops) {
            self.drop_fresh_item_on_slot(ui, slot, &p, view);
        }
        self.populate(ui, view, icons);
    }

    fn inventory_button_drop(
        &mut self,
        ui: &mut UiRoot,
        p: &ItemDragPayload,
        view: &ItemsView<'_>,
    ) {
        if p.obj_id == 0 || p.source_kind.is_alias() {
            return;
        }
        if view.player == 0 || view.get(p.obj_id).is_none() {
            return;
        }
        // The player's own item goes into the preferred pack, anything else
        // straight into the main pack; the session tells them apart.
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::DropOnBackpack(
                ObjectRef::Guid(p.obj_id),
            ))),
        );
    }

    /// Show `view`: the shortcuts' icons and structure, the selection, the
    /// numbers, the combat indicators, the ammunition and the use button.
    pub fn populate(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        let selected = self.selection.get();
        for (i, list) in self.slots.iter().enumerate() {
            let Some(cell) = cell_of(ui, *list) else {
                continue;
            };
            let entry = self.store.get_entry(i as i32).filter(|e| e.object_id != 0);
            let item = entry.and_then(|e| view.get(e.object_id));
            let (icon, drag, tooltip, structure) = match (entry, item) {
                (Some(e), Some(o)) => {
                    let (t, ic, under, over, fx) = if e.object_id == view.player {
                        (ItemType::CONTAINER, PLAYER_PACK_BASE_ICON, 0, 0, 0)
                    } else {
                        (
                            o.item_type,
                            o.icon_id,
                            o.icon_underlay_id,
                            o.icon_overlay_id,
                            o.effects,
                        )
                    };
                    (
                        icons.item_icon(t, ic, under, over, fx),
                        icons.drag_icon(t, ic, under, over, fx),
                        view.tooltip(e.object_id),
                        (o.structure, o.max_structure),
                    )
                }
                _ => (None, None, None, (0, 0)),
            };
            let digits = self.digits.clone();
            if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                match (entry, item) {
                    (Some(e), Some(_)) => s.set_item(e.object_id, icon, drag, Some(e)),
                    _ => s.clear(),
                }
                s.tooltip = tooltip;
                s.set_structure(structure.0, structure.1);
                // Every item cell shows a container's fullness, a pack on
                // the bar too; the bar is shown anew each update, so it
                // follows what goes in and out.
                s.capacity_fill = if s.object_id != 0 {
                    capacity_fill(view, s.object_id)
                } else {
                    -1.0
                };
                s.selected = s.object_id != 0 && Some(s.object_id) == selected;
                s.digits = digits;
                if i < NUMBERED_SLOTS {
                    s.set_shortcut_num(i as i32, view.magic_mode());
                } else {
                    s.clear_shortcut_num();
                }
            }
        }
        self.show_combat_mode(ui, view.combat_mode);
        // The ammunition counter.
        let equipped: Vec<&ClientObject> = if view.player != 0 {
            view.objects.get_equipped_by(view.player)
        } else {
            Vec::new()
        };
        let ammo = ToolbarAmmo::resolve(&equipped);
        self.ammo_object = ammo.object_id;
        if let Some(b) = self.ammo_indicator
            && let Some(w) = ui.widget_mut::<Button>(b)
        {
            w.label = ammo.is_visible().then(|| ammo.display_count.to_string());
        }
        // The use button follows the selection.
        if let Some(b) = self.use_button {
            let enabled = selected.and_then(|s| view.get(s)).is_some_and(|o| {
                ac_core::items::interaction::is_toolbar_use_enabled(
                    o.item_type,
                    o.combat_use.unwrap_or(0),
                    o.useability.unwrap_or(ac_core::items::ItemUseable::UNDEF),
                )
            });
            ui.set_enabled(b, enabled);
        }
    }

    fn handle_slot_message(&mut self, ui: &mut UiRoot, cell: ElementId, message: &ElementMessage) {
        let (id, index) = ui
            .widget::<ItemSlot>(cell)
            .map_or((0, -1), |s| (s.object_id, s.slot_index));
        match message {
            ElementMessage::Pressed if id != 0 => {
                if self.target_mode {
                    if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                        s.consume_primary_press();
                    }
                    offer_target(ui, ObjectRef::Guid(id));
                } else {
                    self.select(ui, id);
                }
            }
            ElementMessage::DoubleClicked if id != 0 => Self::use_item(ui, id),
            ElementMessage::RightClicked if id != 0 => {
                self.select(ui, id);
                emit(
                    ui,
                    PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                        ObjectRef::Guid(id),
                    ))),
                );
            }
            ElementMessage::ItemDragLifted(p) => {
                if p.obj_id != 0 && self.selection.get() != Some(p.obj_id) {
                    self.select(ui, p.obj_id);
                }
                // The lifted shortcut leaves its slot at once.
                emit(ui, PanelOutput::RemoveShortcut(p.source_slot));
                self.store.remove(p.source_slot);
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    s.clear();
                }
            }
            ElementMessage::ItemDragEntered(p) => {
                let a = if p.obj_id != 0 && takes_shortcut_drop(p) {
                    DragAcceptance::Accept
                } else {
                    DragAcceptance::None
                };
                if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                    s.set_drag_acceptance(a);
                }
            }
            ElementMessage::ItemDropped(p) if p.source_kind == ItemDragSource::ShortcutBar => {
                self.drop_shortcut_on_slot(ui, index, p);
            }
            // Judged against the objects at the next update.
            // Another alias (a salvage, vendor or house window cell) is
            // refused before it is judged as an item.
            ElementMessage::ItemDropped(p) if !takes_shortcut_drop(p) => {}
            ElementMessage::ItemDropped(p) => self.fresh_drops.push((index, p.clone())),
            _ => {}
        }
        let selected = self.selection.get();
        for list in self.slots {
            if let Some(c) = cell_of(ui, list)
                && let Some(s) = ui.widget_mut::<ItemSlot>(c)
            {
                s.selected = s.object_id != 0 && Some(s.object_id) == selected;
            }
        }
    }
}

impl PanelController for ToolbarController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if let Some(parent) = ui.parent(element)
            && self.slots.contains(&Some(parent))
        {
            self.handle_slot_message(ui, element, message);
            return true;
        }
        if self.combat_indicators.contains(&Some(element)) {
            if clicked {
                emit(ui, PanelOutput::ToggleCombat);
            }
            return true;
        }
        if Some(element) == self.inventory_button
            && matches!(message, ElementMessage::ItemDropped(_))
        {
            self.queue.push((element, message.clone()));
            return true;
        }
        if let Some(&(panel, _)) = self.panel_buttons.iter().find(|(_, b)| *b == element) {
            if clicked {
                if Some(element) == self.inventory_button && self.target_mode {
                    // In a target mode the backpack button targets the player.
                    offer_target(ui, ObjectRef::Player);
                } else {
                    emit(ui, PanelOutput::TogglePanel(panel));
                }
            }
            return true;
        }
        if Some(element) == self.use_button || Some(element) == self.examine_button {
            if clicked {
                let examine = Some(element) == self.examine_button;
                match (self.selection.get(), examine) {
                    (Some(id), false) => Self::use_item(ui, id),
                    (Some(id), true) => emit(
                        ui,
                        PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                            ObjectRef::Guid(id),
                        ))),
                    ),
                    (None, false) => emit(ui, PanelOutput::EnterUseMode),
                    (None, true) => emit(ui, PanelOutput::EnterExamineMode),
                }
            }
            return true;
        }
        false
    }
}

// ── The stack-split quantity ────────────────────────────────────────────

/// The selected stack's split quantity: a horizontal slider and a typed
/// entry, shown while a stack of more than one is selected. A drag of the
/// selected stack moves this many ([`ItemsView::split`]).
#[derive(Debug)]
pub struct StackSplitController {
    entry: Option<ElementId>,
    slider: Option<ElementId>,
    quantity: StackSplitQuantity,
    current: Option<u32>,
    entry_focused: bool,
}

impl StackSplitController {
    /// Bind to the toolbar's selected-object strip.
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Self {
        let entry = layout
            .find(STACK_SIZE_ENTRY_ID)
            .filter(|e| ui.widget::<Field>(*e).is_some());
        if let Some(e) = entry
            && let Some(f) = ui.widget_mut::<Field>(e)
        {
            f.one_line = true;
            f.right_aligned = true;
            f.selectable = true;
            f.clear_on_submit = false;
            f.record_history = false;
            f.character_filter = Some(Box::new(|c: char| c.is_ascii_digit()));
            f.select_all_on_focus = true;
        }
        let slider = layout
            .find(STACK_SIZE_SLIDER_ID)
            .filter(|e| ui.widget::<Scrollbar>(*e).is_some());
        if let Some(s) = slider
            && let Some(w) = ui.widget_mut::<Scrollbar>(s)
        {
            w.horizontal = true;
            w.scalar = true;
        }
        let c = Self {
            entry,
            slider,
            quantity: StackSplitQuantity::new(),
            current: None,
            entry_focused: false,
        };
        c.show(ui, false);
        c
    }

    /// The split quantity.
    #[must_use]
    pub fn quantity(&self) -> &StackSplitQuantity {
        &self.quantity
    }

    fn show(&self, ui: &mut UiRoot, visible: bool) {
        for e in [self.entry, self.slider].into_iter().flatten() {
            ui.set_visible(e, visible);
        }
    }

    fn reflect(&self, ui: &mut UiRoot, move_slider: bool) {
        if let Some(e) = self.entry
            && let Some(f) = ui.widget_mut::<Field>(e)
        {
            f.set_text(&self.quantity.value().to_string());
        }
        if move_slider
            && let Some(s) = self.slider
            && let Some(w) = ui.widget_mut::<Scrollbar>(s)
        {
            w.set_scalar_position(self.quantity.ratio());
        }
    }

    /// Follow the selection: a stack of more than one shows the controls,
    /// starting at the whole stack (or 1 for an object the vendor's split
    /// rule exempts); anything else hides them.
    pub fn update(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>) {
        let stack = view
            .selected
            .and_then(|s| view.get(s))
            .map_or(1, |o| o.stack_size.max(1).unsigned_abs());
        if view.selected == self.current && stack == self.quantity.maximum() {
            return;
        }
        self.current = view.selected;
        if stack > 1 {
            let seed = if view.vendor_split_exempt { 1 } else { stack };
            self.quantity.reset(stack, Some(seed));
            self.show(ui, true);
        } else {
            self.quantity.reset(1, None);
            self.show(ui, false);
        }
        self.reflect(ui, true);
    }

    /// The quantity back to the whole stack of `stack` units (a panel
    /// refused part of a stack).
    pub fn reset_to_whole(&mut self, ui: &mut UiRoot, stack: u32) {
        self.quantity.reset(stack, None);
        self.reflect(ui, true);
    }

    /// Focus the typed entry (the split key) when `object` is the selected
    /// stack.
    pub fn focus_entry(&self, ui: &mut UiRoot, object: u32) -> bool {
        let Some(e) = self.entry else {
            return false;
        };
        if self.current != Some(object)
            || self.quantity.maximum() <= 1
            || !ui.element(e).is_some_and(|e| e.visible)
        {
            return false;
        }
        ui.set_keyboard_focus(Some(e));
        if let Some(f) = ui.widget_mut::<Field>(e) {
            f.select_all_text();
        }
        true
    }

    fn commit_entry(&mut self, ui: &mut UiRoot) {
        let text = self
            .entry
            .and_then(|e| ui.widget::<Field>(e))
            .map(Field::text);
        self.quantity.set_from_text(text.as_deref());
        self.reflect(ui, true);
    }
}

impl PanelController for StackSplitController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.slider {
            if let ElementMessage::ScalarChanged(p) = message {
                self.quantity.set_from_slider_ratio(*p);
                // The slider keeps where the player put it.
                self.reflect(ui, false);
            }
            return true;
        }
        if Some(element) == self.entry {
            if let ElementMessage::Submitted(_) = message {
                self.commit_entry(ui);
            }
            return matches!(
                message,
                ElementMessage::Submitted(_) | ElementMessage::TextChanged(_)
            );
        }
        false
    }

    fn on_descendant_focus_changed(&mut self, ui: &mut UiRoot, focused: Option<ElementId>) {
        let now = focused.is_some() && focused == self.entry;
        if self.entry_focused && !now {
            // Leaving the entry commits what was typed.
            self.commit_entry(ui);
        }
        self.entry_focused = now;
    }
}

/// The toolbar window: the toolbar and the selected stack's split
/// controls, as one window controller.
#[derive(Debug)]
pub struct ToolbarWindowController {
    /// The shortcut bar and buttons.
    pub toolbar: ToolbarController,
    /// The split quantity.
    pub split: StackSplitController,
    /// The selected object's name, flash and bars.
    pub selected: Option<crate::panels::selected_object::SelectedObjectController>,
}

impl ToolbarWindowController {
    /// Resolve queued input against `view` and show it.
    pub fn update(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        self.toolbar.update(ui, view, icons);
        self.split.update(ui, view);
    }
}

impl PanelController for ToolbarWindowController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        self.toolbar.on_element_message(ui, element, message)
            || self.split.on_element_message(ui, element, message)
    }

    fn on_descendant_focus_changed(&mut self, ui: &mut UiRoot, focused: Option<ElementId>) {
        self.split.on_descendant_focus_changed(ui, focused);
    }

    fn tick(&mut self, ui: &mut UiRoot, dt: f64) {
        if let Some(s) = self.selected.as_mut() {
            s.tick(ui, dt);
        }
    }

    fn dispose(&mut self, _ui: &mut UiRoot) {
        if let Some(s) = self.selected.as_mut() {
            s.dispose();
        }
    }
}
