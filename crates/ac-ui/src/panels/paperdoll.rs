//! The paperdoll: the equipment slots around the figure (one single-cell
//! item list per body location, with the retail empty-slot art), the
//! Aetheria slots shown as they unlock, the slots/doll view toggle, and the
//! mask over the rendered figure that maps a pixel to a body location
//! through the dat's click map.
//!
//! Like the inventory, the paperdoll reads an [`ItemsView`] at each update
//! and queues what the player does. The figure mask is a pointer
//! region: what it lifts and shows while dragged comes from the snapshot
//! the last update left in it; its clicks and drops are queued in it and
//! resolved at the next update.

use std::any::Any;
use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::paperdoll::upper_inventory_objects;
use ac_core::items::{AetheriaUnlockState, ClientObject, EquipMask, ItemType, aetheria_unlocks};
use ac_dat::DatCollection;
use ac_dat::objects::{EnumIdMap, Palette, RenderSurface};
use ac_dat::texture::{DecodeOptions, decode};
use ac_runtime::inventory::{ItemCommand, ObjectRef, is_auto_wear_item};
use ac_runtime::session::SessionCommand;

use crate::draw::{Color, TextureRef};
use crate::event::{DragAcceptance, DragPayload, ItemDragPayload, ItemDragSource};
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::{ImportedLayout, LayoutSource};
use crate::panels::PanelOutput;
use crate::panels::cell_template::prototype_empty_sprite;
use crate::panels::inventory::{PLAYER_PACK_BASE_ICON, SelectionEcho};
use crate::panels::items::{ItemsView, emit, fill_slot, offer_target};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{DragGhost, ElementId, PointerRegion, RegionHandler};
use crate::widgets::{Button, ItemList, ItemSlot, LabelAlign};

/// The 3-D figure viewport.
pub const DOLL_VIEWPORT_ID: u32 = 0x1000_01D5;
/// The mask over the figure.
pub const DOLL_DRAG_MASK_ID: u32 = 0x1000_01D6;
/// The slots/doll view toggle.
pub const SLOTS_BUTTON_ID: u32 = 0x1000_05BE;
/// The armor slots shown only in the slot view.
pub const ARMOR_SLOT_ELEMENT_IDS: [u32; 9] = [
    0x1000_05AB,
    0x1000_05AC,
    0x1000_05AD,
    0x1000_05AE,
    0x1000_05AF,
    0x1000_05B0,
    0x1000_05B1,
    0x1000_05B2,
    0x1000_05B3,
];
/// The master enum map slot of the interface surfaces.
pub const INTERFACE_ENUM_CATEGORY: u32 = 7;
/// The click map's entry in that map.
pub const CLICK_MAP_ENUM: u32 = 0x1000_000C;
/// The size of a ghost lifted from the figure, pixels.
pub const DOLL_DRAG_GHOST_SIZE: i32 = 32;

const WEAPON_SLOT_MASK: EquipMask = EquipMask(
    EquipMask::MELEE_WEAPON.bits()
        | EquipMask::MISSILE_WEAPON.bits()
        | EquipMask::HELD.bits()
        | EquipMask::TWO_HANDED.bits(),
);

/// One equipment slot: its element, the body locations it shows, the
/// catalog prototype carrying its empty art, and the Aetheria bit that
/// unlocks it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SlotDefinition {
    /// The slot's item list.
    pub element: u32,
    /// The locations it shows.
    pub mask: EquipMask,
    /// The prototype carrying its empty-slot image.
    pub empty_prototype: u32,
    /// The Aetheria bit that shows it (none: always shown).
    pub unlock: AetheriaUnlockState,
}

const fn def(element: u32, mask: EquipMask, empty_prototype: u32) -> SlotDefinition {
    SlotDefinition {
        element,
        mask,
        empty_prototype,
        unlock: AetheriaUnlockState::NONE,
    }
}

/// The slots, in the order a lift reports as its source slot.
pub const SLOT_DEFINITIONS: [SlotDefinition; 24] = [
    def(0x1000_05AB, EquipMask::HEAD_WEAR, 0x1000_05B4),
    def(0x1000_01E2, EquipMask::CHEST_WEAR, 0x1000_044E),
    def(0x1000_01E3, EquipMask::UPPER_LEG_WEAR, 0x1000_044F),
    def(0x1000_05B0, EquipMask::HAND_WEAR, 0x1000_05B9),
    def(0x1000_05B3, EquipMask::FOOT_WEAR, 0x1000_05BD),
    def(0x1000_05AC, EquipMask::CHEST_ARMOR, 0x1000_05B5),
    def(0x1000_05AD, EquipMask::ABDOMEN_ARMOR, 0x1000_05B6),
    def(0x1000_05AE, EquipMask::UPPER_ARM_ARMOR, 0x1000_05B7),
    def(0x1000_05AF, EquipMask::LOWER_ARM_ARMOR, 0x1000_05B8),
    def(0x1000_05B1, EquipMask::UPPER_LEG_ARMOR, 0x1000_05BA),
    def(0x1000_05B2, EquipMask::LOWER_LEG_ARMOR, 0x1000_05BB),
    def(0x1000_01DA, EquipMask::NECK_WEAR, 0x1000_0446),
    def(0x1000_01DB, EquipMask::WRIST_WEAR_LEFT, 0x1000_0447),
    def(0x1000_01DD, EquipMask::WRIST_WEAR_RIGHT, 0x1000_0449),
    def(0x1000_01DC, EquipMask::FINGER_WEAR_LEFT, 0x1000_0448),
    def(0x1000_01DE, EquipMask::FINGER_WEAR_RIGHT, 0x1000_044A),
    def(0x1000_01E1, EquipMask::SHIELD, 0x1000_044D),
    def(0x1000_01E0, EquipMask::MISSILE_AMMO, 0x1000_044C),
    def(0x1000_01DF, WEAPON_SLOT_MASK, 0x1000_044B),
    def(0x1000_058E, EquipMask::TRINKET_ONE, 0x1000_058F),
    def(0x1000_05E9, EquipMask::CLOAK, 0x1000_05EA),
    SlotDefinition {
        element: 0x1000_0595,
        mask: EquipMask::SIGIL_ONE,
        empty_prototype: 0x1000_0592,
        unlock: AetheriaUnlockState::BLUE,
    },
    SlotDefinition {
        element: 0x1000_0596,
        mask: EquipMask::SIGIL_TWO,
        empty_prototype: 0x1000_0593,
        unlock: AetheriaUnlockState::YELLOW,
    },
    SlotDefinition {
        element: 0x1000_0597,
        mask: EquipMask::SIGIL_THREE,
        empty_prototype: 0x1000_0594,
        unlock: AetheriaUnlockState::RED,
    },
];

/// The authored empty art of every slot, by slot element (from the dat's
/// cell prototypes; a slot whose prototype has none is left out).
#[must_use]
pub fn resolve_empty_sprites(source: &dyn LayoutSource) -> HashMap<u32, u32> {
    SLOT_DEFINITIONS
        .iter()
        .filter_map(|d| {
            let s = prototype_empty_sprite(source, d.empty_prototype);
            (s != 0).then_some((d.element, s))
        })
        .collect()
}

/// The prototype carrying slot `element`'s empty art.
#[must_use]
pub fn slot_prototype(element: u32) -> Option<u32> {
    SLOT_DEFINITIONS
        .iter()
        .find(|d| d.element == element)
        .map(|d| d.empty_prototype)
}

/// The slot covering a body location (its definition index), so a lift
/// from the figure reports the same source slot the slot would.
#[must_use]
pub fn slot_index_of(location: EquipMask) -> i32 {
    SLOT_DEFINITIONS
        .iter()
        .position(|d| d.mask.intersects(location))
        .map_or(-1, |i| i as i32)
}

/// Whether a drop on a slot showing `target_mask` is an off-hand weapon
/// wield: the shield slot also takes anything that fits the melee weapon
/// slot, although the item's own locations do not name the shield slot.
/// Whether the character may dual wield is the server's decision.
#[must_use]
pub fn is_off_hand_weapon_drop(item: &ClientObject, target_mask: EquipMask) -> bool {
    target_mask == EquipMask::SHIELD && item.valid_locations.intersects(EquipMask::MELEE_WEAPON)
}

/// The locations a slot showing `target_mask` may take `item` in: its own
/// valid locations, plus the shield slot for an off-hand weapon drop.
#[must_use]
pub fn paperdoll_slot_valid_locations(item: &ClientObject, target_mask: EquipMask) -> EquipMask {
    if is_off_hand_weapon_drop(item, target_mask) {
        item.valid_locations | EquipMask::SHIELD
    } else {
        item.valid_locations
    }
}

/// The locations a drop on a slot showing `target_mask` wields `item` at:
/// none unless the item fits there; all its locations for armor and
/// clothing (the server chooses); else only the slot's.
#[must_use]
pub fn paperdoll_drop_wield_mask(item: &ClientObject, target_mask: EquipMask) -> EquipMask {
    if !item.valid_locations.intersects(target_mask) {
        return EquipMask::NONE;
    }
    if is_auto_wear_item(item) {
        item.valid_locations
    } else {
        EquipMask(item.valid_locations.bits() & target_mask.bits())
    }
}

// ── The click map ───────────────────────────────────────────────────────

/// The figure's click map: one colour per body location, sampled one pixel
/// per pixel (no scaling).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PaperdollClickMap {
    rgba: Vec<u8>,
    width: u32,
    height: u32,
}

impl PaperdollClickMap {
    /// A map of `width` × `height` RGBA pixels (`None` when the buffer is
    /// too small or the map empty).
    #[must_use]
    pub fn new(rgba: Vec<u8>, width: u32, height: u32) -> Option<Self> {
        let needed = (width as usize)
            .checked_mul(height as usize)?
            .checked_mul(4)?;
        (width > 0 && height > 0 && rgba.len() >= needed).then_some(Self {
            rgba,
            width,
            height,
        })
    }

    /// The click map from the dats (master map slot 7, entry 0x1000000C).
    #[must_use]
    pub fn load(dats: &DatCollection) -> Option<Self> {
        let master_id = dats.portal.header().master_map_id.cast_unsigned();
        let master: EnumIdMap = dats.get(master_id).ok().flatten()?;
        let sub: EnumIdMap = dats
            .get(master.client_id(INTERFACE_ENUM_CATEGORY)?)
            .ok()
            .flatten()?;
        let surface: RenderSurface = dats.get(sub.client_id(CLICK_MAP_ENUM)?).ok().flatten()?;
        let palette: Option<Palette> = (surface.default_palette_id != 0)
            .then(|| dats.get(surface.default_palette_id).ok().flatten())
            .flatten();
        let t = decode(&surface, palette.as_ref(), DecodeOptions::default()).ok()?;
        (t.width > 1 && t.height > 1)
            .then(|| Self::new(t.rgba, t.width, t.height))
            .flatten()
    }

    /// Width, pixels.
    #[must_use]
    pub fn width(&self) -> u32 {
        self.width
    }

    /// Height, pixels.
    #[must_use]
    pub fn height(&self) -> u32 {
        self.height
    }

    /// The body location at local (`x`, `y`) (none off the map or on an
    /// unmapped colour).
    #[must_use]
    pub fn body_location(&self, x: i32, y: i32) -> EquipMask {
        if x < 0 || y < 0 || x as u32 >= self.width || y as u32 >= self.height {
            return EquipMask::NONE;
        }
        let i = (y as usize * self.width as usize + x as usize) * 4;
        let mask = |a: EquipMask, b: EquipMask| EquipMask(a.bits() | b.bits());
        match (self.rgba[i], self.rgba[i + 1], self.rgba[i + 2]) {
            (0x00, 0x00, 0xFF) => EquipMask::HEAD_WEAR,
            (0x00, 0xFF, 0x00) => mask(EquipMask::CHEST_WEAR, EquipMask::CHEST_ARMOR),
            (0xFF, 0x00, 0x00) => mask(EquipMask::ABDOMEN_WEAR, EquipMask::ABDOMEN_ARMOR),
            (0x00, 0xFF, 0xFF) => mask(EquipMask::UPPER_ARM_WEAR, EquipMask::UPPER_ARM_ARMOR),
            (0xFF, 0x00, 0xFF) => mask(EquipMask::LOWER_ARM_WEAR, EquipMask::LOWER_ARM_ARMOR),
            (0xFF, 0xFF, 0x00) => mask(EquipMask::UPPER_LEG_WEAR, EquipMask::UPPER_LEG_ARMOR),
            (0x00, 0x00, 0x80) => mask(EquipMask::LOWER_LEG_WEAR, EquipMask::LOWER_LEG_ARMOR),
            (0x00, 0x80, 0x00) => EquipMask::HAND_WEAR,
            (0x80, 0x00, 0x00) => EquipMask::FOOT_WEAR,
            _ => EquipMask::NONE,
        }
    }
}

/// The nine body locations the click map names (wear and armor paired).
pub const CLICK_MAP_LOCATIONS: [EquipMask; 9] = [
    EquipMask::HEAD_WEAR,
    EquipMask(EquipMask::CHEST_WEAR.bits() | EquipMask::CHEST_ARMOR.bits()),
    EquipMask(EquipMask::ABDOMEN_WEAR.bits() | EquipMask::ABDOMEN_ARMOR.bits()),
    EquipMask(EquipMask::UPPER_ARM_WEAR.bits() | EquipMask::UPPER_ARM_ARMOR.bits()),
    EquipMask(EquipMask::LOWER_ARM_WEAR.bits() | EquipMask::LOWER_ARM_ARMOR.bits()),
    EquipMask::HAND_WEAR,
    EquipMask(EquipMask::UPPER_LEG_WEAR.bits() | EquipMask::UPPER_LEG_ARMOR.bits()),
    EquipMask(EquipMask::LOWER_LEG_WEAR.bits() | EquipMask::LOWER_LEG_ARMOR.bits()),
    EquipMask::FOOT_WEAR,
];

/// The figure parts each of [`CLICK_MAP_LOCATIONS`] is drawn from.
const PARTS_BY_LOCATION: [&[u32]; 9] = [
    &[0x10],
    &[0x09],
    &[0x00],
    &[0x0A, 0x0D],
    &[0x0B, 0x0E],
    &[0x0C, 0x0F],
    &[0x01, 0x05],
    &[0x02, 0x06],
    &[0x03, 0x07, 0x04, 0x08],
];

/// What to flash on the figure for a selection.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct FigureFlash {
    /// The whole figure (the player selected himself).
    pub whole_figure: bool,
    /// One bit per figure part.
    pub part_mask: u32,
}

impl FigureFlash {
    /// Anything to flash.
    #[must_use]
    pub fn any(&self) -> bool {
        self.whole_figure || self.part_mask != 0
    }
}

/// The figure parts the selected object covers: the whole figure for the
/// player, the parts of every location an item is the outermost worn thing
/// at, nothing otherwise.
#[must_use]
pub fn figure_flash(
    objects: &ac_core::items::ClientObjectTable,
    player: u32,
    selected: u32,
) -> FigureFlash {
    if player == 0 || selected == 0 {
        return FigureFlash::default();
    }
    if selected == player {
        return FigureFlash {
            whole_figure: true,
            part_mask: 0,
        };
    }
    let outermost = upper_inventory_objects(objects, player, &CLICK_MAP_LOCATIONS);
    let mut part_mask = 0;
    for (i, &o) in outermost.iter().enumerate() {
        if o == selected {
            for &p in PARTS_BY_LOCATION[i] {
                part_mask |= 1 << p;
            }
        }
    }
    FigureFlash {
        whole_figure: false,
        part_mask,
    }
}

// ── The figure mask ─────────────────────────────────────────────────────

/// What the player did on the figure, waiting for the next update.
#[derive(Debug, Clone, PartialEq)]
pub enum DollEvent {
    /// A click at the local point.
    Clicked(i32, i32),
    /// A right click.
    RightClicked(i32, i32),
    /// A drag entered.
    DragOver(Option<ItemDragPayload>),
    /// The drag left.
    DragLeft,
    /// A drop.
    Dropped(Option<ItemDragPayload>),
}

/// The figure mask's region: the click map, the object at each of its
/// locations as of the last update (the player where nothing is worn), the
/// ghost of each, and the queue of what happened.
#[derive(Debug, Default)]
pub struct DollRegion {
    click_map: Option<Arc<PaperdollClickMap>>,
    player: u32,
    outermost: [u32; 9],
    ghosts: HashMap<u32, TextureRef>,
    events: Vec<DollEvent>,
}

impl DollRegion {
    fn location_index(&self, x: i32, y: i32) -> Option<(usize, EquipMask)> {
        let loc = self.click_map.as_ref()?.body_location(x, y);
        CLICK_MAP_LOCATIONS
            .iter()
            .position(|l| *l == loc)
            .map(|i| (i, loc))
    }

    /// The object at local (`x`, `y`) and its body location (0 off the
    /// map).
    #[must_use]
    pub fn object_at(&self, x: i32, y: i32) -> (u32, EquipMask) {
        match self.location_index(x, y) {
            Some((i, loc)) => (self.outermost[i], loc),
            None => (0, EquipMask::NONE),
        }
    }

    /// What a lift at local (`x`, `y`) carries: the worn item as its
    /// equipment slot would lift it, the player's own pack (as the first
    /// pack slot lifts it) where nothing is worn, nothing off the map.
    #[must_use]
    pub fn payload_at(&self, x: i32, y: i32) -> Option<ItemDragPayload> {
        let (hit, loc) = self.object_at(x, y);
        if hit == 0 {
            return None;
        }
        Some(if hit == self.player {
            ItemDragPayload::new(hit, ItemDragSource::Inventory, 0)
        } else {
            ItemDragPayload::new(hit, ItemDragSource::Equipment, slot_index_of(loc))
        })
    }

    /// Take what happened since the last update.
    pub fn take_events(&mut self) -> Vec<DollEvent> {
        std::mem::take(&mut self.events)
    }
}

impl RegionHandler for DollRegion {
    fn clicked(&mut self, x: i32, y: i32) -> bool {
        self.events.push(DollEvent::Clicked(x, y));
        true
    }

    fn handles_click(&self) -> bool {
        true
    }

    fn right_clicked(&mut self, x: i32, y: i32) -> bool {
        self.events.push(DollEvent::RightClicked(x, y));
        true
    }

    fn is_drag_source(&self) -> bool {
        true
    }

    fn drag_payload_at(&mut self, x: i32, y: i32) -> Option<DragPayload> {
        self.payload_at(x, y).map(DragPayload::new)
    }

    fn drag_ghost_at(&mut self, x: i32, y: i32) -> Option<DragGhost> {
        let (hit, _) = self.object_at(x, y);
        self.ghosts.get(&hit).map(|t| DragGhost {
            texture: *t,
            width: DOLL_DRAG_GHOST_SIZE,
            height: DOLL_DRAG_GHOST_SIZE,
        })
    }

    fn drag_over_at(&mut self, payload: Option<&DragPayload>, _x: i32, _y: i32) -> bool {
        let p = payload.and_then(|p| p.get::<ItemDragPayload>()).cloned();
        self.events.push(DollEvent::DragOver(p));
        true
    }

    fn drag_left(&mut self) -> bool {
        self.events.push(DollEvent::DragLeft);
        true
    }

    fn drop_released_at(&mut self, payload: Option<&DragPayload>, _x: i32, _y: i32) -> bool {
        let p = payload.and_then(|p| p.get::<ItemDragPayload>()).cloned();
        self.events.push(DollEvent::Dropped(p));
        true
    }
}

fn doll_region(ui: &mut UiRoot, mask: ElementId) -> Option<&mut DollRegion> {
    let region = ui.element_mut(mask)?.pointer_region.as_mut()?;
    let handler: &mut dyn Any = region.handler.as_mut();
    handler.downcast_mut::<DollRegion>()
}

// ── The controller ──────────────────────────────────────────────────────

/// The paperdoll's controller.
#[derive(Debug)]
pub struct PaperdollController {
    slots: Vec<(usize, ElementId)>,
    armor_slots: Vec<ElementId>,
    doll_viewport: Option<ElementId>,
    doll_mask: Option<ElementId>,
    slots_button: Option<ElementId>,
    slot_view: bool,
    selection: SelectionEcho,
    target_source: Option<u32>,
    target_mode: bool,
    pending_cache: Vec<u32>,
    player: u32,
    queue: Vec<(ElementId, ElementMessage)>,
    doll_drag_acceptance: DragAcceptance,
    last_flash_selection: Option<Option<u32>>,
}

impl PaperdollController {
    /// Bind to the inventory window's paperdoll half. `empty_sprite` is the
    /// fallback empty art; `empty_sprites` the per-slot art
    /// ([`resolve_empty_sprites`]).
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        empty_sprite: u32,
        empty_sprites: &HashMap<u32, u32>,
        click_map: Option<Arc<PaperdollClickMap>>,
        font: Option<Arc<crate::UiFont>>,
    ) -> Self {
        let mut slots = Vec::new();
        for (i, d) in SLOT_DEFINITIONS.iter().enumerate() {
            let Some(list) = layout
                .find(d.element)
                .filter(|l| ui.widget::<ItemList>(*l).is_some())
            else {
                continue;
            };
            let cell = ui.widget::<ItemList>(list).and_then(|l| l.item(0));
            if let Some(c) = cell
                && let Some(s) = ui.widget_mut::<ItemSlot>(c)
            {
                s.source_kind = ItemDragSource::Equipment;
                s.slot_index = i as i32;
                s.empty_sprite = empty_sprites
                    .get(&d.element)
                    .copied()
                    .unwrap_or(empty_sprite);
            }
            slots.push((i, list));
        }
        let armor_slots = ARMOR_SLOT_ELEMENT_IDS
            .iter()
            .filter_map(|id| layout.find(*id))
            .filter(|l| ui.widget::<ItemList>(*l).is_some())
            .collect();
        let doll_viewport = layout.find(DOLL_VIEWPORT_ID);
        let doll_mask = layout.find(DOLL_DRAG_MASK_ID);
        if let Some(m) = doll_mask
            && let Some(e) = ui.element_mut(m)
        {
            // The mask carries no face of its own; bind the behaviour to the
            // element, whatever class it imported as.
            e.click_through = false;
            e.pointer_region = Some(PointerRegion::new(DollRegion {
                click_map,
                ..DollRegion::default()
            }));
        }
        let slots_button = layout
            .find(SLOTS_BUTTON_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some());
        if let Some(b) = slots_button
            && let Some(w) = ui.widget_mut::<Button>(b)
        {
            w.listeners.click = true;
            if font.is_some() {
                w.label = Some("Slots".to_owned());
                w.label_font = font;
                w.label_color = Color::WHITE;
                w.label_align = LabelAlign::Left;
            }
        }
        let c = Self {
            slots,
            armor_slots,
            doll_viewport,
            doll_mask,
            slots_button,
            slot_view: false,
            selection: SelectionEcho::default(),
            target_source: None,
            target_mode: false,
            pending_cache: Vec::new(),
            player: 0,
            queue: Vec::new(),
            doll_drag_acceptance: DragAcceptance::None,
            last_flash_selection: None,
        };
        c.apply_view(ui);
        c
    }

    /// The slot view shows (else the doll view).
    #[must_use]
    pub fn slot_view(&self) -> bool {
        self.slot_view
    }

    /// Whether a drag now over the figure would be worn on release (state
    /// the panel keeps rather than paints; the authored overlay art is not
    /// bound).
    #[must_use]
    pub fn doll_drag_acceptance(&self) -> DragAcceptance {
        self.doll_drag_acceptance
    }

    /// The figure mask.
    #[must_use]
    pub fn doll_mask(&self) -> Option<ElementId> {
        self.doll_mask
    }

    /// The item list of slot element `element`.
    #[must_use]
    pub fn slot_list(&self, element: u32) -> Option<ElementId> {
        self.slots
            .iter()
            .find(|(i, _)| SLOT_DEFINITIONS[*i].element == element)
            .map(|(_, l)| *l)
    }

    fn toggle(&mut self, ui: &mut UiRoot) {
        self.slot_view = !self.slot_view;
        self.apply_view(ui);
    }

    fn apply_view(&self, ui: &mut UiRoot) {
        let doll = !self.slot_view;
        for e in [self.doll_viewport, self.doll_mask].into_iter().flatten() {
            ui.set_visible(e, doll);
        }
        for a in &self.armor_slots {
            ui.set_visible(*a, self.slot_view);
        }
    }

    fn slot_of(&self, ui: &UiRoot, cell: ElementId) -> Option<(usize, ElementId)> {
        let parent = ui.parent(cell)?;
        self.slots.iter().copied().find(|(_, l)| *l == parent)
    }

    fn select(&mut self, ui: &mut UiRoot, id: u32) {
        if id == 0 {
            return;
        }
        self.selection.select(id);
        emit(ui, PanelOutput::SelectObject(id));
    }

    fn examine(&mut self, ui: &mut UiRoot, id: u32) {
        self.select(ui, id);
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Appraise(
                ObjectRef::Guid(id),
            ))),
        );
    }

    fn wield(ui: &mut UiRoot, item: u32, mask: EquipMask) {
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Item(ItemCommand::Wield {
                item: ObjectRef::Guid(item),
                location: mask.bits(),
            })),
        );
    }

    /// Resolve what was queued against `view`, then show it.
    pub fn update(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        self.player = view.player;
        self.target_source = view.target_source;
        self.target_mode = view.target_mode;
        self.pending_cache = view.pending_sources.to_vec();
        self.selection.sync(view.selected);
        for (element, message) in std::mem::take(&mut self.queue) {
            self.handle(ui, element, &message, view);
        }
        let events = self
            .doll_mask
            .and_then(|m| doll_region(ui, m))
            .map(DollRegion::take_events)
            .unwrap_or_default();
        for e in events {
            self.handle_doll(ui, e, view);
        }
        self.populate(ui, view, icons);
    }

    fn handle(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
        view: &ItemsView<'_>,
    ) {
        let Some((index, _)) = self.slot_of(ui, element) else {
            return;
        };
        let mask = SLOT_DEFINITIONS[index].mask;
        match message {
            ElementMessage::ItemDragEntered(p) => {
                let a = if p.source_kind.is_alias() {
                    DragAcceptance::None
                } else {
                    match view.get(p.obj_id) {
                        Some(item)
                            if paperdoll_slot_valid_locations(item, mask).intersects(mask) =>
                        {
                            DragAcceptance::Accept
                        }
                        _ => DragAcceptance::Reject,
                    }
                };
                if let Some(s) = ui.widget_mut::<ItemSlot>(element) {
                    s.set_drag_acceptance(a);
                }
            }
            ElementMessage::ItemDropped(p) => {
                if p.source_kind.is_alias() {
                    return;
                }
                let Some(item) = view.get(p.obj_id) else {
                    return;
                };
                // An off-hand wield asks for the shield slot; whatever already
                // sits there goes to the backpack first.
                let off_hand = is_off_hand_weapon_drop(item, mask);
                let wield = paperdoll_drop_wield_mask(item, mask);
                if off_hand || wield != EquipMask::NONE {
                    if !view.can_make_request {
                        emit(
                            ui,
                            PanelOutput::Notice(ac_runtime::inventory::REQUEST_BUSY.to_owned()),
                        );
                    } else if off_hand {
                        emit(
                            ui,
                            PanelOutput::Session(SessionCommand::Item(
                                ItemCommand::WieldSecondary(ObjectRef::Guid(p.obj_id)),
                            )),
                        );
                    } else {
                        Self::wield(ui, p.obj_id, wield);
                    }
                }
            }
            _ => {}
        }
    }

    fn handle_doll(&mut self, ui: &mut UiRoot, e: DollEvent, view: &ItemsView<'_>) {
        let object_at = |ui: &mut UiRoot, x, y| {
            self.doll_mask
                .and_then(|m| doll_region(ui, m))
                .map_or(0, |r| r.object_at(x, y).0)
        };
        match e {
            DollEvent::Clicked(x, y) => {
                let hit = object_at(ui, x, y);
                if hit == 0 {
                    return;
                }
                if self.target_mode {
                    offer_target(ui, ObjectRef::Player);
                    return;
                }
                self.select(ui, hit);
            }
            DollEvent::RightClicked(x, y) => {
                let hit = object_at(ui, x, y);
                if hit != 0 {
                    self.examine(ui, hit);
                }
            }
            DollEvent::DragOver(p) => {
                self.doll_drag_acceptance = match p {
                    Some(p) if !p.source_kind.is_alias() => {
                        if view.get(p.obj_id).is_some_and(is_auto_wear_item) {
                            DragAcceptance::Accept
                        } else {
                            DragAcceptance::Reject
                        }
                    }
                    _ => DragAcceptance::None,
                };
            }
            DollEvent::DragLeft => self.doll_drag_acceptance = DragAcceptance::None,
            DollEvent::Dropped(p) => {
                self.doll_drag_acceptance = DragAcceptance::None;
                let Some(p) = p.filter(|p| !p.source_kind.is_alias()) else {
                    return;
                };
                if let Some(item) = view.get(p.obj_id).filter(|i| is_auto_wear_item(i)) {
                    // Worn without naming a location: the server chooses.
                    Self::wield(ui, p.obj_id, item.valid_locations);
                }
            }
        }
    }

    /// Show `view`: each slot's outermost worn item, the Aetheria slots as
    /// unlocked, the selection, and the figure mask's snapshot.
    pub fn populate(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        let p = view.player;
        let equipped: Vec<&ClientObject> = view
            .objects
            .objects()
            .filter(|o| {
                o.currently_equipped_location != EquipMask::NONE
                    && (o.wielder_id == p || o.container_id == p)
            })
            .collect();
        let unlocked = aetheria_unlocks(view.get(p));
        for &(index, list) in &self.slots {
            let d = SLOT_DEFINITIONS[index];
            let worn = equipped
                .iter()
                .find(|o| o.currently_equipped_location.intersects(d.mask))
                .copied();
            let cell = ui.widget::<ItemList>(list).and_then(|l| l.item(0));
            if let Some(c) = cell
                && let Some(s) = ui.widget_mut::<ItemSlot>(c)
            {
                fill_slot(s, worn, view, icons);
                s.set_waiting(worn.is_some_and(|w| view.is_pending(w.object_id())));
            }
            if d.unlock != AetheriaUnlockState::NONE {
                ui.set_visible(list, unlocked.intersects(d.unlock));
            }
        }
        self.apply_selection(ui);
        // The figure mask's snapshot: what each location lifts and shows.
        let outermost = upper_inventory_objects(view.objects, p, &CLICK_MAP_LOCATIONS);
        let mut ghosts = HashMap::new();
        for &o in &outermost {
            if o == 0 || ghosts.contains_key(&o) {
                continue;
            }
            let (t, icon, under, over, effects) = if o == p {
                (ItemType::CONTAINER, PLAYER_PACK_BASE_ICON, 0, 0, 0)
            } else if let Some(i) = view.get(o) {
                (
                    i.item_type,
                    i.icon_id,
                    i.icon_underlay_id,
                    i.icon_overlay_id,
                    i.effects,
                )
            } else {
                continue;
            };
            let ghost = icons
                .drag_icon(t, icon, under, over, effects)
                .or_else(|| icons.item_icon(t, icon, under, over, effects));
            if let Some(g) = ghost {
                ghosts.insert(o, g);
            }
        }
        if let Some(r) = self.doll_mask.and_then(|m| doll_region(ui, m)) {
            r.player = p;
            for (slot, o) in r.outermost.iter_mut().zip(outermost) {
                *slot = o;
            }
            r.ghosts = ghosts;
        }
        // Any selection change flashes the parts of the figure it covers.
        if self.last_flash_selection != Some(view.selected) {
            self.last_flash_selection = Some(view.selected);
            let flash = figure_flash(view.objects, p, view.selected.unwrap_or(0));
            if flash.any() {
                emit(
                    ui,
                    PanelOutput::FlashFigure {
                        whole_figure: flash.whole_figure,
                        part_mask: flash.part_mask,
                    },
                );
            }
        }
    }

    fn apply_selection(&self, ui: &mut UiRoot) {
        let selected = self.selection.get();
        for &(_, list) in &self.slots {
            let cell = ui.widget::<ItemList>(list).and_then(|l| l.item(0));
            if let Some(c) = cell
                && let Some(s) = ui.widget_mut::<ItemSlot>(c)
            {
                let id = s.object_id;
                s.selected = id != 0 && Some(id) == selected && !self.pending_cache.contains(&id);
            }
        }
    }

    fn press(&mut self, ui: &mut UiRoot, cell: ElementId) {
        let Some(id) = ui
            .widget::<ItemSlot>(cell)
            .map(|s| s.object_id)
            .filter(|id| *id != 0)
        else {
            return;
        };
        if self.target_mode {
            if let Some(s) = ui.widget_mut::<ItemSlot>(cell) {
                s.consume_primary_press();
            }
            offer_target(ui, ObjectRef::Guid(id));
            return;
        }
        self.select(ui, id);
    }
}

impl PanelController for PaperdollController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.slots_button {
            if matches!(message, ElementMessage::Clicked { .. }) {
                self.toggle(ui);
            }
            return true;
        }
        if self.slot_of(ui, element).is_none() {
            return false;
        }
        let id = ui.widget::<ItemSlot>(element).map_or(0, |s| s.object_id);
        match message {
            ElementMessage::Pressed => self.press(ui, element),
            ElementMessage::DoubleClicked if id != 0 => emit(
                ui,
                PanelOutput::Session(SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(id)))),
            ),
            ElementMessage::RightClicked if id != 0 => self.examine(ui, id),
            ElementMessage::ItemDragLifted(p)
                if p.obj_id != 0 && self.selection.get() != Some(p.obj_id) =>
            {
                self.select(ui, p.obj_id);
            }
            ElementMessage::ItemDragEntered(_) | ElementMessage::ItemDropped(_) => {
                self.queue.push((element, message.clone()));
                return true;
            }
            _ => return true,
        }
        self.apply_selection(ui);
        true
    }
}

/// The inventory window's two halves, the pack panel and the paperdoll, as
/// one window controller (the window has one controller; each half hears
/// the messages of its own elements).
#[derive(Debug)]
pub struct InventoryWindowController {
    /// The packs, burden and captions.
    pub inventory: crate::panels::InventoryController,
    /// The equipment slots and the figure.
    pub paperdoll: PaperdollController,
}

impl InventoryWindowController {
    /// Resolve both halves' queued input against `view`, then show it.
    pub fn update(&mut self, ui: &mut UiRoot, view: &ItemsView<'_>, icons: &mut dyn IconSource) {
        self.inventory.update(ui, view, icons);
        self.paperdoll.update(ui, view, icons);
    }
}

impl PanelController for InventoryWindowController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        self.inventory.on_element_message(ui, element, message)
            || self.paperdoll.on_element_message(ui, element, message)
    }
}
