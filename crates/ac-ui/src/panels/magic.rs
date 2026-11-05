//! The magic panels: the spellbook window (layout
//! 0x21000034: the learned spells by school and level filter, selection,
//! dragging a spell onto a favourite bar, forgetting a spell) and the spell
//! bars of the combat window (layout 0x21000073: eight
//! favourite tabs, the cast button, the selected spell's name, the
//! wielded caster's endowment).
//!
//! Both read the player's [`Spellbook`] and the object table from a
//! [`MagicView`] at each update. Casting, the favourite bars and the
//! filters go out as the runtime's gameplay commands.

use std::sync::Arc;

use ac_core::items::{ClientObject, ClientObjectTable, EquipMask, ItemType, ItemUseable};
use ac_core::spells::{MagicSchool, SpellMetadata, Spellbook};
use ac_runtime::gameplay::GameplayCommand;
use ac_runtime::gameplay::magic::SpellCastGate;
use ac_runtime::inventory::{ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;

use crate::draw::Color;
use crate::element_info::ElementInfo;
use crate::event::{DragAcceptance, DragPayload};
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::{ImportedLayout, LayoutSource, import_infos_rooted};
use crate::panels::PanelOutput;
use crate::panels::items::{bind_click, emit};
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::catalog_slot::catalog_element;
use crate::widgets::{
    Button, CatalogSlot, ItemList, ScrollSource, Scrollbar, ShortcutDigits, Text,
};

/// The spellbook's layout.
pub const SPELLBOOK_LAYOUT_ID: u32 = 0x2100_0034;
/// The spellbook window's root.
pub const SPELLBOOK_ROOT_ID: u32 = 0x1000_02A8;
/// The spells tab.
pub const SPELL_TAB_ID: u32 = 0x1000_02A9;
/// The components tab.
pub const COMPONENT_TAB_ID: u32 = 0x1000_02AA;
/// The close button.
pub const CLOSE_ID: u32 = 0x1000_02AB;
/// The spells page.
pub const SPELL_PAGE_ID: u32 = 0x1000_02AC;
/// The components page.
pub const COMPONENT_PAGE_ID: u32 = 0x1000_02AD;
/// The spell list.
pub const SPELL_LIST_ID: u32 = 0x1000_0295;
/// The spell list's scrollbar.
pub const SPELL_SCROLLBAR_ID: u32 = 0x1000_0296;
/// Forget the selected spell.
pub const DELETE_BUTTON_ID: u32 = 0x1000_02A5;
/// The school and level filter buttons and their bits.
pub const FILTER_BUTTONS: [(u32, u32); 13] = [
    (0x1000_0298, 0x0001),
    (0x1000_0299, 0x0002),
    (0x1000_029A, 0x0004),
    (0x1000_029B, 0x0008),
    (0x1000_05C0, 0x2000),
    (0x1000_029C, 0x0010),
    (0x1000_029D, 0x0020),
    (0x1000_029E, 0x0040),
    (0x1000_029F, 0x0080),
    (0x1000_02A0, 0x0100),
    (0x1000_02A1, 0x0200),
    (0x1000_02A2, 0x0400),
    (0x1000_054E, 0x0800),
];

/// The combat window's layout (the spell bars live in it).
pub const COMBAT_LAYOUT_ID: u32 = 0x2100_0073;
/// The magic page.
pub const SPELLCASTING_PAGE_ID: u32 = 0x1000_0061;
/// The selected spell's name.
pub const SPELL_NAME_ID: u32 = 0x1000_048B;
/// The endowment cell.
pub const ENDOWMENT_ID: u32 = 0x1000_00B1;
/// The cast button.
pub const CAST_BUTTON_ID: u32 = 0x1000_00B2;
/// A bar's scrollbar.
pub const FAVORITE_SCROLLBAR_ID: u32 = 0x1000_00B5;
/// A bar's list.
pub const FAVORITE_LIST_ID: u32 = 0x1000_00B6;
/// The eight tab buttons.
pub const FAVORITE_TAB_IDS: [u32; 8] = [
    0x1000_00A3,
    0x1000_00A4,
    0x1000_00A5,
    0x1000_00A6,
    0x1000_00A7,
    0x1000_00A8,
    0x1000_00A9,
    0x1000_05C2,
];
/// The eight bar groups.
pub const FAVORITE_GROUP_IDS: [u32; 8] = [
    0x1000_00AA,
    0x1000_00AB,
    0x1000_00AC,
    0x1000_00AD,
    0x1000_00AE,
    0x1000_00AF,
    0x1000_00B0,
    0x1000_05C3,
];
/// A favourite cell, pixels.
pub const FAVORITE_CELL_PX: f32 = 32.0;

/// A spell dragged off a favourite bar.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SpellFavoritePayload {
    /// The bar it left.
    pub source_tab: usize,
    /// Its position there.
    pub source_position: usize,
    /// The spell.
    pub spell_id: u32,
}

/// A spell dragged out of the spellbook (the spell stays learned).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SpellbookShortcutPayload {
    /// The spell.
    pub spell_id: u32,
}

/// A favourite cell takes spells (from a bar or the spellbook), and has no
/// opinion of anything else.
#[must_use]
pub fn favorite_drag_acceptance(p: &DragPayload) -> DragAcceptance {
    if p.get::<SpellFavoritePayload>().is_some() || p.get::<SpellbookShortcutPayload>().is_some() {
        DragAcceptance::Accept
    } else {
        DragAcceptance::None
    }
}

/// Spell-cast key actions.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SpellKey {
    /// Cast favourite `n` (0-based) of the open bar.
    UseSlot(usize),
    /// The previous bar.
    PrevTab,
    /// The next bar.
    NextTab,
    /// The first bar.
    FirstTab,
    /// The last bar.
    LastTab,
    /// The previous spell.
    PrevSpell,
    /// The next spell.
    NextSpell,
    /// The first spell.
    FirstSpell,
    /// The last spell.
    LastSpell,
    /// Cast the selected spell.
    CastCurrent,
}

/// The favourite slot a retail spell-slot key names (keys 1..=12).
#[must_use]
pub fn spell_slot_key(key: usize) -> Option<SpellKey> {
    (1..=12).contains(&key).then(|| SpellKey::UseSlot(key - 1))
}

/// What the magic panels read, borrowed for one update.
#[derive(Clone, Copy)]
pub struct MagicView<'a> {
    /// The player's spellbook (learned spells, favourites, filters, the
    /// spell table).
    pub spellbook: &'a Spellbook,
    /// The object table.
    pub objects: &'a ClientObjectTable,
    /// The player.
    pub player: u32,
    /// The selected object.
    pub selected: Option<u32>,
    /// Whether spell `id` can be cast at the selection now.
    pub cast_gate: Option<&'a dyn Fn(u32) -> SpellCastGate>,
    /// The spell's level for the level filters (default: the metadata's
    /// generation, which the dat projector fills from the formula).
    pub spell_level: Option<&'a dyn Fn(u32) -> i32>,
}

impl std::fmt::Debug for MagicView<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MagicView")
            .field("player", &self.player)
            .field("selected", &self.selected)
            .finish_non_exhaustive()
    }
}

impl<'a> MagicView<'a> {
    /// A view of `spellbook` and `objects`.
    #[must_use]
    pub fn new(spellbook: &'a Spellbook, objects: &'a ClientObjectTable, player: u32) -> Self {
        Self {
            spellbook,
            objects,
            player,
            selected: None,
            cast_gate: None,
            spell_level: None,
        }
    }

    fn metadata(&self, id: u32) -> Option<&'a SpellMetadata> {
        self.spellbook.try_get_metadata(id)
    }

    fn level(&self, id: u32) -> i32 {
        match self.spell_level {
            Some(f) => f(id),
            None => self.metadata(id).map_or(0, |m| m.generation),
        }
    }
}

fn gameplay(ui: &mut UiRoot, c: GameplayCommand) {
    emit(ui, PanelOutput::Session(SessionCommand::Gameplay(c)));
}

/// The school filter bit of a school.
#[must_use]
pub fn school_filter_bit(school: MagicSchool) -> u32 {
    match school {
        MagicSchool::CREATURE_ENCHANTMENT => 0x0001,
        MagicSchool::ITEM_ENCHANTMENT => 0x0002,
        MagicSchool::LIFE_MAGIC => 0x0004,
        MagicSchool::WAR_MAGIC => 0x0008,
        MagicSchool::VOID_MAGIC => 0x2000,
        _ => 0,
    }
}

/// Whether a spell passes the filters: its school's bit and its level's
/// bit (levels 1..=8 from 0x10 up) must both be set.
#[must_use]
pub fn spell_visible(school: MagicSchool, level: i32, filters: u32) -> bool {
    let school = school_filter_bit(school);
    if !(1..=8).contains(&level) {
        return false;
    }
    let level = 0x10 << (level - 1);
    school != 0 && filters & school != 0 && filters & level != 0
}

// ── The spellbook row style ─────────────────────────────────────────────

/// The authored spellbook row (catalog prototype 0x10000343): its size,
/// icon and label boxes, font and art.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SpellbookRowStyle {
    /// Width.
    pub width: f32,
    /// Height.
    pub height: f32,
    /// Icon left.
    pub icon_left: f32,
    /// Icon top.
    pub icon_top: f32,
    /// Icon width.
    pub icon_width: f32,
    /// Icon height.
    pub icon_height: f32,
    /// Label left.
    pub label_left: f32,
    /// Label width.
    pub label_width: f32,
    /// The label's font.
    pub font_did: u32,
    /// The label's colour.
    pub label_color: Color,
    /// The row art.
    pub background_sprite: u32,
    /// The selection art.
    pub selected_sprite: u32,
}

impl SpellbookRowStyle {
    /// The catalog layout.
    pub const CATALOG_LAYOUT_ID: u32 = 0x2100_0037;
    /// The row prototype.
    pub const PROTOTYPE_ID: u32 = 0x1000_0343;
    /// The selection overlay.
    pub const SELECTED_OVERLAY_ID: u32 = 0x1000_0342;
    /// The label.
    pub const LABEL_ID: u32 = 0x1000_0344;
    /// The icon.
    pub const ICON_ID: u32 = 0x1000_033B;

    /// The style from the dats (`None` when a piece is missing).
    #[must_use]
    pub fn load(source: &dyn LayoutSource) -> Option<Self> {
        let proto = import_infos_rooted(source, Self::CATALOG_LAYOUT_ID, Self::PROTOTYPE_ID)?;
        Self::from_info(&proto)
    }

    /// The style from the prototype's description.
    #[must_use]
    pub fn from_info(proto: &ElementInfo) -> Option<Self> {
        fn find(e: &ElementInfo, id: u32) -> Option<&ElementInfo> {
            if e.id == id {
                return Some(e);
            }
            e.children.iter().find_map(|c| find(c, id))
        }
        let selected = find(proto, Self::SELECTED_OVERLAY_ID)?;
        let label = find(proto, Self::LABEL_ID)?;
        let icon = find(proto, Self::ICON_ID)?;
        let background = proto.state_media.get("")?.file;
        let selection = selected.state_media.get("")?.file;
        if proto.width <= 0.0 || proto.height <= 0.0 {
            return None;
        }
        Some(Self {
            width: proto.width,
            height: proto.height,
            icon_left: icon.x,
            icon_top: icon.y,
            icon_width: icon.width,
            icon_height: icon.height,
            label_left: label.x,
            label_width: label.width,
            font_did: label.font_did,
            label_color: label.font_color.unwrap_or(Color::WHITE),
            background_sprite: background,
            selected_sprite: selection,
        })
    }
}

fn list_cells(ui: &UiRoot, list: ElementId) -> Vec<ElementId> {
    ui.widget::<ItemList>(list)
        .map(|l| l.cells().to_vec())
        .unwrap_or_default()
}

fn set_open(ui: &mut UiRoot, tab: ElementId, open: bool) {
    crate::widgets::tab_panel::set_open(ui, tab, open);
}

// ── The spellbook window ────────────────────────────────────────────────

/// The spellbook window's pages.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SpellbookPage {
    /// The learned spells.
    #[default]
    Spells,
    /// The spell components.
    Components,
}

/// The spellbook window's controller.
#[derive(Debug)]
pub struct SpellbookWindowController {
    spell_page: ElementId,
    component_page: ElementId,
    spell_tab: ElementId,
    component_tab: ElementId,
    close: ElementId,
    delete: ElementId,
    list: ElementId,
    filters: Vec<(ElementId, u32)>,
    style: SpellbookRowStyle,
    font: Option<Arc<crate::UiFont>>,
    page: SpellbookPage,
    selected: Option<u32>,
    shown: Option<(u64, u32)>,
    spell_names: Vec<(u32, String)>,
    filter_bits: u32,
    components: Option<crate::panels::spell_components::ComponentsPageController>,
}

impl SpellbookWindowController {
    /// Bind to an imported spellbook window (`None` when a required piece
    /// is missing).
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        style: SpellbookRowStyle,
        font: Option<Arc<crate::UiFont>>,
    ) -> Option<Self> {
        let spell_page = layout.find(SPELL_PAGE_ID)?;
        let component_page = layout.find(COMPONENT_PAGE_ID)?;
        let spell_tab = layout.find(SPELL_TAB_ID)?;
        let component_tab = layout.find(COMPONENT_TAB_ID)?;
        let close = layout
            .find(CLOSE_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some())?;
        let delete = layout
            .find(DELETE_BUTTON_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some())?;
        let list = layout
            .find(SPELL_LIST_ID)
            .filter(|l| ui.widget::<ItemList>(*l).is_some())?;
        for e in [spell_tab, component_tab, close, delete] {
            bind_click(ui, e);
        }
        let mut filters = Vec::new();
        for (id, mask) in FILTER_BUTTONS {
            if let Some(b) = layout
                .find(id)
                .filter(|b| ui.widget::<Button>(*b).is_some())
            {
                bind_click(ui, b);
                filters.push((b, mask));
            }
        }
        let width = ui.element(list).map_or(1.0, |e| e.width.max(1.0));
        ui.with::<ItemList, _>(list, |l, ui| {
            l.flush(ui);
            l.columns = 1;
            l.cell_width = width;
            l.cell_height = style.height;
        });
        if let Some(bar) = layout.find(SPELL_SCROLLBAR_ID)
            && let Some(b) = ui.widget_mut::<Scrollbar>(bar)
        {
            b.model = ScrollSource::Element(list);
        }
        let mut c = Self {
            spell_page,
            component_page,
            spell_tab,
            component_tab,
            close,
            delete,
            list,
            filters,
            style,
            font,
            page: SpellbookPage::Spells,
            selected: None,
            shown: None,
            spell_names: Vec::new(),
            filter_bits: 0,
            components: None,
        };
        c.show_page(ui, SpellbookPage::Spells);
        Some(c)
    }

    /// Give the components page its controller.
    pub fn attach_components(
        &mut self,
        components: crate::panels::spell_components::ComponentsPageController,
    ) {
        self.components = Some(components);
    }

    /// The components page.
    pub fn components_mut(
        &mut self,
    ) -> Option<&mut crate::panels::spell_components::ComponentsPageController> {
        self.components.as_mut()
    }

    /// The page showing.
    #[must_use]
    pub fn page(&self) -> SpellbookPage {
        self.page
    }

    /// The selected spell.
    #[must_use]
    pub fn selected(&self) -> Option<u32> {
        self.selected
    }

    /// The spell list.
    #[must_use]
    pub fn list(&self) -> ElementId {
        self.list
    }

    /// Show `page`.
    pub fn show_page(&mut self, ui: &mut UiRoot, page: SpellbookPage) {
        self.page = page;
        ui.set_visible(self.spell_page, page == SpellbookPage::Spells);
        ui.set_visible(self.component_page, page == SpellbookPage::Components);
        set_open(ui, self.spell_tab, page == SpellbookPage::Spells);
        set_open(ui, self.component_tab, page == SpellbookPage::Components);
    }

    /// Show `view`: the learned spells that pass the filters, in display
    /// order (rebuilt when the book or the filters changed).
    pub fn update(&mut self, ui: &mut UiRoot, view: &MagicView<'_>, icons: &mut dyn IconSource) {
        let book = view.spellbook;
        let key = (book.spellbook_revision(), book.spellbook_filters());
        if self.shown == Some(key) {
            return;
        }
        self.shown = Some(key);
        let filters = book.spellbook_filters();
        self.filter_bits = filters;
        for &(b, mask) in &self.filters {
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, filters & mask != 0));
        }
        let mut spells: Vec<&SpellMetadata> = book
            .learned_spells()
            .filter_map(|id| view.metadata(id))
            .filter(|m| spell_visible(m.school_id, view.level(m.spell_id), filters))
            .collect();
        spells.sort_by_key(|m| (m.sort_key, m.spell_id));
        self.spell_names = spells
            .iter()
            .map(|m| (m.spell_id, m.name.clone()))
            .collect();
        let list = self.list;
        let (w, s) = (
            ui.element(list).map_or(1.0, |e| e.width.max(1.0)),
            self.style,
        );
        ui.with::<ItemList, _>(list, |l, ui| {
            l.defer_layout();
            l.flush(ui);
        });
        for m in spells {
            let slot = CatalogSlot {
                entry_id: m.spell_id,
                icon: icons.spell_icon(m.spell_id),
                label: m.name.clone(),
                show_label: true,
                background_sprite: s.background_sprite,
                selected_sprite: s.selected_sprite,
                selection_behind_content: true,
                label_font: self.font.clone(),
                label_color: s.label_color,
                icon_left: s.icon_left,
                icon_top: s.icon_top,
                icon_width: s.icon_width,
                icon_height: s.icon_height,
                label_left: s.label_left,
                label_width: s.label_width,
                drag_payload: Some(DragPayload::new(SpellbookShortcutPayload {
                    spell_id: m.spell_id,
                })),
                ..CatalogSlot::default()
            };
            let cell = ui.insert(catalog_element(w, s.height), slot);
            ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
        }
        ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
        self.sync_selection(ui);
    }

    fn sync_selection(&self, ui: &mut UiRoot) {
        for c in list_cells(ui, self.list) {
            if let Some(s) = ui.widget_mut::<CatalogSlot>(c) {
                s.selected = Some(s.entry_id) == self.selected;
            }
        }
    }

    fn select(&mut self, ui: &mut UiRoot, spell: u32) {
        self.selected = Some(spell);
        self.sync_selection(ui);
        let list = self.list;
        let index = list_cells(ui, list).iter().position(|c| {
            ui.widget::<CatalogSlot>(*c)
                .is_some_and(|s| s.entry_id == spell)
        });
        if let Some(i) = index {
            ui.with::<ItemList, _>(list, |l, ui| l.scroll_item_into_view(ui, list, i));
        }
    }

    fn request_delete(&self, ui: &mut UiRoot) {
        let Some(spell) = self.selected else {
            return;
        };
        let Some(name) = self
            .spell_names
            .iter()
            .find(|(id, _)| *id == spell)
            .map(|(_, n)| n.clone())
        else {
            return;
        };
        emit(
            ui,
            PanelOutput::Confirm {
                message: format!(
                    "Are you sure you want to remove {name} from your spellbook? \
                     You will no longer be able to cast this spell unless you learn it again!"
                ),
                on_yes: Box::new(PanelOutput::Session(SessionCommand::Gameplay(
                    GameplayCommand::ForgetSpell(spell.to_string()),
                ))),
            },
        );
    }
}

impl PanelController for SpellbookWindowController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if let Some(c) = self.components.as_mut()
            && c.on_message(ui, element, message)
        {
            return true;
        }
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if ui.parent(element) == Some(self.list) {
            let id = ui.widget::<CatalogSlot>(element).map_or(0, |s| s.entry_id);
            if id == 0 {
                return false;
            }
            match message {
                ElementMessage::Clicked { .. } | ElementMessage::CatalogDragLifted(_) => {
                    self.select(ui, id);
                }
                ElementMessage::DoubleClicked => {
                    self.select(ui, id);
                    emit(ui, PanelOutput::FavoriteSpell(id));
                }
                ElementMessage::RightClicked => emit(ui, PanelOutput::ExamineSpell(id)),
                _ => {}
            }
            return true;
        }
        if element == self.spell_tab || element == self.component_tab {
            if clicked {
                let page = if element == self.spell_tab {
                    SpellbookPage::Spells
                } else {
                    SpellbookPage::Components
                };
                self.show_page(ui, page);
            }
            return true;
        }
        if element == self.close {
            if clicked {
                emit(ui, PanelOutput::ClosePanel(crate::panels::panel_id::MAGIC));
            }
            return true;
        }
        if element == self.delete {
            if clicked {
                self.request_delete(ui);
            }
            return true;
        }
        if let Some(&(_, mask)) = self.filters.iter().find(|(b, _)| *b == element) {
            if clicked {
                // Two toggles before the book answers both count.
                self.filter_bits ^= mask;
                gameplay(ui, GameplayCommand::SetSpellbookFilter(self.filter_bits));
                if let Some(l) = ui.widget_mut::<ItemList>(self.list) {
                    l.scroll.set_scroll_y(0);
                }
            }
            return true;
        }
        false
    }
}

// ── The spell bars ──────────────────────────────────────────────────────

/// The spell bars' controller (the magic page of the combat window).
#[derive(Debug)]
pub struct SpellcastingController {
    tabs: [ElementId; 8],
    groups: [ElementId; 8],
    lists: [Option<ElementId>; 8],
    cast: ElementId,
    spell_name: Option<ElementId>,
    endowment_host: ElementId,
    endowment_slot: ElementId,
    digits: Option<Arc<ShortcutDigits>>,
    empty_sprite: u32,
    active_tab: usize,
    selected: [Option<u32>; 8],
    endowment_selected: [bool; 8],
    endowment_item: u32,
    endowment_spell: u32,
    shown_revision: Option<u64>,
    favorites: [Vec<u32>; 8],
    names: std::collections::HashMap<u32, String>,
    /// Each bar's width when last laid out.
    viewport_widths: [f32; 8],
}

/// Scroll favourite list `list` until `spell`'s cell shows.
fn scroll_spell_into_view(ui: &mut UiRoot, list: ElementId, spell: u32) {
    let index = list_cells(ui, list).iter().position(|c| {
        ui.widget::<CatalogSlot>(*c)
            .is_some_and(|s| s.entry_id == spell)
    });
    if let Some(i) = index {
        ui.with::<ItemList, _>(list, |l, ui| l.scroll_item_into_view(ui, list, i));
    }
}

/// Favourite cells a bar shows without scrolling, once fitted.
pub const VISIBLE_FAVORITE_SLOTS: usize = 18;

fn apply_descendant_layout(ui: &mut UiRoot, parent: ElementId) {
    let Some((w, h)) = ui.element(parent).map(|e| (e.width, e.height)) else {
        return;
    };
    for c in ui.children(parent) {
        ui.apply_anchor(c, w, h);
        apply_descendant_layout(ui, c);
    }
}

/// Widen (or narrow) the combat window so the favourite bar shows
/// `visible` cells of `cell` pixels; returns the window's new width. The
/// authored window shows fewer than the client offers.
pub fn fit_favorite_slots(
    ui: &mut UiRoot,
    layout: &ImportedLayout,
    visible: usize,
    cell: f32,
) -> Option<f32> {
    let root = layout.root;
    apply_descendant_layout(ui, root);
    let list = layout
        .find(FAVORITE_LIST_ID)
        .filter(|l| ui.widget::<ItemList>(*l).is_some())?;
    let list_width = ui.element(list)?.width;
    let target = visible as f32 * cell;
    let e = ui.element_mut(root)?;
    e.width = (e.width + target - list_width).max(0.0);
    let width = e.width;
    apply_descendant_layout(ui, root);
    Some(width)
}

fn descendants(ui: &UiRoot, root: ElementId, out: &mut Vec<ElementId>) {
    for c in ui.children(root) {
        out.push(c);
        descendants(ui, c, out);
    }
}

impl SpellcastingController {
    /// Bind to the imported combat window (`None` when the magic controls
    /// are missing). `empty_sprite` is the favourite cells' empty art
    /// ([`crate::panels::cell_template::empty_sprite_from_info`] for list
    /// 0x100000B6).
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        digits: Option<Arc<ShortcutDigits>>,
        empty_sprite: u32,
    ) -> Option<Self> {
        let cast = layout
            .find(CAST_BUTTON_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some())?;
        let endowment_host = layout.find(ENDOWMENT_ID)?;
        let mut tabs = [cast; 8];
        let mut groups = [cast; 8];
        let mut lists = [None; 8];
        for i in 0..8 {
            tabs[i] = layout.find(FAVORITE_TAB_IDS[i])?;
            groups[i] = layout.find(FAVORITE_GROUP_IDS[i])?;
            bind_click(ui, tabs[i]);
            let mut d = Vec::new();
            descendants(ui, groups[i], &mut d);
            lists[i] = d
                .iter()
                .copied()
                .find(|e| ui.widget::<ItemList>(*e).is_some());
            let bar = d.iter().copied().find(|e| {
                ui.widget::<Scrollbar>(*e).is_some()
                    && ui
                        .element(*e)
                        .is_some_and(|el| el.dat_element_id == FAVORITE_SCROLLBAR_ID)
            });
            if let Some(l) = lists[i] {
                if let Some(w) = ui.widget_mut::<ItemList>(l) {
                    w.single_row = true;
                    w.horizontal_scroll = true;
                    w.cell_width = FAVORITE_CELL_PX;
                    w.cell_height = FAVORITE_CELL_PX;
                    w.fill_visible_empty_slots = true;
                    w.accepts_catalog_drops = true;
                    let empty = empty_sprite;
                    w.empty_slot_factory = Some(Box::new(move |ui: &mut UiRoot| {
                        ui.insert(
                            catalog_element(FAVORITE_CELL_PX, FAVORITE_CELL_PX),
                            CatalogSlot {
                                empty_sprite: empty,
                                drag_accepts: Some(favorite_drag_acceptance),
                                ..CatalogSlot::default()
                            },
                        )
                    }));
                }
                if let Some(b) = bar
                    && let Some(w) = ui.widget_mut::<Scrollbar>(b)
                {
                    w.horizontal = true;
                    w.model = ScrollSource::Element(l);
                }
            }
        }
        if let Some(w) = ui.widget_mut::<Button>(cast) {
            w.listeners.click = true;
        }
        for c in ui.children(endowment_host) {
            ui.set_visible(c, false);
        }
        let (w, h) = ui
            .element(endowment_host)
            .map_or((32.0, 32.0), |e| (e.width, e.height));
        let endowment_slot = ui.add(
            endowment_host,
            catalog_element(w, h),
            CatalogSlot::default(),
        );
        let spell_name = layout
            .find(SPELL_NAME_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some());
        if let Some(t) = spell_name
            && let Some(w) = ui.widget_mut::<Text>(t)
        {
            w.one_line = true;
            w.centered = true;
            w.padding = 0.0;
        }
        let mut c = Self {
            tabs,
            groups,
            lists,
            cast,
            spell_name,
            endowment_host,
            endowment_slot,
            digits,
            empty_sprite,
            active_tab: 0,
            selected: [None; 8],
            endowment_selected: [false; 8],
            endowment_item: 0,
            endowment_spell: 0,
            shown_revision: None,
            favorites: Default::default(),
            names: std::collections::HashMap::new(),
            viewport_widths: std::array::from_fn(|t| {
                lists[t]
                    .and_then(|l| ui.element(l))
                    .map_or(0.0, |e| e.width)
            }),
        };
        c.select_tab(ui, 0);
        Some(c)
    }

    /// The bar showing.
    #[must_use]
    pub fn active_tab(&self) -> usize {
        self.active_tab
    }

    /// The selected spell of the open bar.
    #[must_use]
    pub fn selected_spell(&self) -> Option<u32> {
        self.selected[self.active_tab]
    }

    /// The endowment is selected on the open bar.
    #[must_use]
    pub fn endowment_selected(&self) -> bool {
        self.endowment_selected[self.active_tab] && self.endowment_item != 0
    }

    /// The list of bar `tab`.
    #[must_use]
    pub fn list(&self, tab: usize) -> Option<ElementId> {
        self.lists.get(tab).copied().flatten()
    }

    /// The endowment cell.
    #[must_use]
    pub fn endowment_slot(&self) -> ElementId {
        self.endowment_slot
    }

    /// Show `view`: rebuild the bars when the book changed (not while a
    /// drag is in the air), follow the wielded caster, and say whether the
    /// cast button can cast.
    pub fn update(&mut self, ui: &mut UiRoot, view: &MagicView<'_>, icons: &mut dyn IconSource) {
        let revision = view.spellbook.spellbook_revision();
        if self.shown_revision != Some(revision) && ui.drag_payload().is_none() {
            self.shown_revision = Some(revision);
            self.favorites = std::array::from_fn(|t| view.spellbook.get_favorites(t).to_vec());
            self.names = view
                .spellbook
                .learned_spells()
                .filter_map(|id| view.metadata(id).map(|m| (id, m.name.clone())))
                .collect();
            self.rebuild(ui, view, icons);
        }
        // Empty cells a widened bar grew get their numbers too.
        self.layout_bars(ui);
        self.update_endowment(ui, view, icons);
        self.select_tab(ui, self.active_tab);
        self.update_cast(ui, view);
    }

    fn rebuild(&mut self, ui: &mut UiRoot, view: &MagicView<'_>, icons: &mut dyn IconSource) {
        for tab in 0..8 {
            let Some(list) = self.lists[tab] else {
                continue;
            };
            ui.with::<ItemList, _>(list, |l, ui| {
                l.defer_layout();
                l.flush(ui);
            });
            for (position, &id) in self.favorites[tab].clone().iter().enumerate() {
                let meta = view.metadata(id);
                let slot = CatalogSlot {
                    entry_id: id,
                    icon: meta.and_then(|_| icons.spell_icon(id)),
                    label: meta.map_or_else(|| format!("Spell {id}"), |m| m.name.clone()),
                    empty_sprite: self.empty_sprite,
                    drag_payload: Some(DragPayload::new(SpellFavoritePayload {
                        source_tab: tab,
                        source_position: position,
                        spell_id: id,
                    })),
                    drag_accepts: Some(favorite_drag_acceptance),
                    ..CatalogSlot::default()
                };
                let cell = ui.insert(catalog_element(FAVORITE_CELL_PX, FAVORITE_CELL_PX), slot);
                ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
            }
            ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
            self.stamp_digits(ui, list);
        }
    }

    /// Number the first nine cells of a bar (regular digits over a spell,
    /// the empty digits over an empty cell).
    fn stamp_digits(&self, ui: &mut UiRoot, list: ElementId) {
        for (i, c) in list_cells(ui, list).into_iter().enumerate() {
            if let Some(s) = ui.widget_mut::<CatalogSlot>(c) {
                s.digits = self.digits.clone();
                s.shortcut_num = if i < 9 { i as i32 } else { -1 };
                s.empty_sprite = self.empty_sprite;
            }
        }
    }

    /// Lay the bars out (an empty tail fills a widened bar) and renumber;
    /// a bar whose width changed brings its selected spell back into view.
    pub fn layout_bars(&mut self, ui: &mut UiRoot) {
        for tab in 0..self.lists.len() {
            let Some(list) = self.lists[tab] else {
                continue;
            };
            ui.with::<ItemList, _>(list, |l, ui| l.layout_cells(ui, list));
            let width = ui.element(list).map_or(0.0, |e| e.width);
            if self.viewport_widths[tab] != width {
                self.viewport_widths[tab] = width;
                if !self.endowment_selected[tab]
                    && let Some(spell) = self.selected[tab]
                {
                    scroll_spell_into_view(ui, list, spell);
                    ui.with::<ItemList, _>(list, |l, ui| l.layout_cells(ui, list));
                }
            }
            self.stamp_digits(ui, list);
        }
    }

    fn update_endowment(
        &mut self,
        ui: &mut UiRoot,
        view: &MagicView<'_>,
        icons: &mut dyn IconSource,
    ) {
        let equipped = if view.player != 0 {
            view.objects.get_equipped_by(view.player)
        } else {
            Vec::new()
        };
        let endowment: Option<&ClientObject> = equipped.into_iter().find(|i| {
            i.currently_equipped_location.intersects(EquipMask::HELD)
                && i.item_type.intersects(ItemType::CASTER)
                && i.spell_id.unwrap_or(0) != 0
        });
        self.endowment_item = endowment.map_or(0, ClientObject::object_id);
        self.endowment_spell = endowment.and_then(|e| e.spell_id).unwrap_or(0);
        ui.set_visible(self.endowment_host, endowment.is_some());
        let spell_name = view.metadata(self.endowment_spell).map_or_else(
            || format!("Spell {}", self.endowment_spell),
            |m| m.name.clone(),
        );
        let icon = (self.endowment_spell != 0)
            .then(|| icons.spell_icon(self.endowment_spell))
            .flatten();
        let overlay = endowment.and_then(|e| {
            icons.drag_icon(
                e.item_type,
                e.icon_id,
                e.icon_underlay_id,
                e.icon_overlay_id,
                e.effects,
            )
        });
        let label = endowment.map_or_else(String::new, |e| {
            format!("{} ({spell_name})", e.appropriate_name())
        });
        if let Some(s) = ui.widget_mut::<CatalogSlot>(self.endowment_slot) {
            s.entry_id = self.endowment_item;
            s.icon = icon;
            s.overlay = overlay;
            s.label = label;
        }
        for tab in 0..8 {
            if self.endowment_item != 0 && self.selected[tab].is_none() {
                self.endowment_selected[tab] = true;
            } else if self.endowment_item == 0 {
                self.endowment_selected[tab] = false;
            }
        }
    }

    fn select_tab(&mut self, ui: &mut UiRoot, tab: usize) {
        self.active_tab = tab.min(7);
        for i in 0..8 {
            set_open(ui, self.tabs[i], i == self.active_tab);
            ui.set_visible(self.groups[i], i == self.active_tab);
        }
        let t = self.active_tab;
        let favorites = &self.favorites[t];
        if self.endowment_selected[t] && self.endowment_item != 0 {
            self.selected[t] = None;
        } else if self.selected[t].is_none_or(|s| !favorites.contains(&s)) {
            self.selected[t] = favorites.first().copied();
            self.endowment_selected[t] = favorites.is_empty() && self.endowment_item != 0;
        }
        self.sync_selection(ui);
    }

    fn sync_selection(&self, ui: &mut UiRoot) {
        for (tab, list) in self.lists.iter().enumerate() {
            let Some(list) = *list else {
                continue;
            };
            for c in list_cells(ui, list) {
                if let Some(s) = ui.widget_mut::<CatalogSlot>(c) {
                    s.selected = s.entry_id != 0 && Some(s.entry_id) == self.selected[tab];
                }
            }
        }
        let endowment = self.endowment_item != 0 && self.endowment_selected[self.active_tab];
        if let Some(s) = ui.widget_mut::<CatalogSlot>(self.endowment_slot) {
            s.selected = endowment;
        }
        let chosen = if self.endowment_selected[self.active_tab] {
            (self.endowment_spell != 0).then_some(self.endowment_spell)
        } else {
            self.selected[self.active_tab]
        };
        let name = chosen
            .and_then(|id| self.names.get(&id).cloned())
            .unwrap_or_default();
        if let Some(t) = self.spell_name
            && let Some(w) = ui.widget_mut::<Text>(t)
        {
            let color = w.default_color;
            w.set_lines(vec![Line::new(name, color)]);
        }
    }

    fn select_spell(&mut self, ui: &mut UiRoot, spell: u32) {
        let t = self.active_tab;
        self.endowment_selected[t] = false;
        self.selected[t] = Some(spell);
        self.sync_selection(ui);
        if let Some(list) = self.lists[t] {
            scroll_spell_into_view(ui, list, spell);
        }
    }

    fn select_endowment(&mut self, ui: &mut UiRoot) {
        if self.endowment_item == 0 {
            return;
        }
        let t = self.active_tab;
        self.endowment_selected[t] = true;
        self.selected[t] = None;
        self.sync_selection(ui);
    }

    fn cast_selected(&self, ui: &mut UiRoot) {
        let t = self.active_tab;
        if self.endowment_selected[t] && self.endowment_item != 0 {
            emit(
                ui,
                PanelOutput::Session(SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(
                    self.endowment_item,
                )))),
            );
        } else if let Some(spell) = self.selected[t] {
            gameplay(
                ui,
                GameplayCommand::Cast {
                    spell: spell.to_string(),
                    target: None,
                },
            );
        }
    }

    fn move_selection(&mut self, ui: &mut UiRoot, delta: i32, edge: bool) {
        let spells = self.favorites[self.active_tab].clone();
        let has_endowment = self.endowment_item != 0;
        let count = spells.len() + usize::from(has_endowment);
        if count == 0 {
            return;
        }
        let offset = i64::from(has_endowment);
        let current: i64 = if self.endowment_selected[self.active_tab] {
            0
        } else {
            self.selected[self.active_tab]
                .and_then(|id| spells.iter().position(|s| *s == id))
                .map_or(-1, |p| p as i64)
                + offset
        };
        let count = count as i64;
        let next = if edge {
            if delta < 0 { count - 1 } else { 0 }
        } else {
            (current + i64::from(delta) + count).rem_euclid(count)
        };
        if has_endowment && next == 0 {
            self.select_endowment(ui);
        } else {
            self.select_spell(ui, spells[(next - offset) as usize]);
        }
    }

    /// A spell key ([`spell_slot_key`] and the tab and spell steps).
    pub fn handle_key(&mut self, ui: &mut UiRoot, key: SpellKey) -> bool {
        match key {
            SpellKey::UseSlot(i) => {
                if let Some(&spell) = self.favorites[self.active_tab].get(i) {
                    self.select_spell(ui, spell);
                    self.cast_selected(ui);
                }
            }
            SpellKey::PrevTab => self.select_tab(ui, (self.active_tab + 7) % 8),
            SpellKey::NextTab => self.select_tab(ui, (self.active_tab + 1) % 8),
            SpellKey::FirstTab => self.select_tab(ui, 0),
            SpellKey::LastTab => self.select_tab(ui, 7),
            SpellKey::PrevSpell => self.move_selection(ui, -1, false),
            SpellKey::NextSpell => self.move_selection(ui, 1, false),
            SpellKey::FirstSpell => self.move_selection(ui, 0, true),
            SpellKey::LastSpell => self.move_selection(ui, -1, true),
            SpellKey::CastCurrent => self.cast_selected(ui),
        }
        true
    }

    /// Put `spell` at the end of the open bar (or just select it when it
    /// is there already): a double click in the spellbook.
    pub fn add_favorite(&mut self, ui: &mut UiRoot, spell: u32) {
        let t = self.active_tab;
        if !self.favorites[t].contains(&spell) {
            gameplay(
                ui,
                GameplayCommand::AddFavorite {
                    tab: t,
                    position: self.favorites[t].len(),
                    spell: spell.to_string(),
                },
            );
        }
        self.select_spell(ui, spell);
    }

    fn update_cast(&self, ui: &mut UiRoot, view: &MagicView<'_>) {
        let (enabled, tooltip) = self.cast_state(view);
        ui.set_enabled(self.cast, enabled);
        if let Some(w) = ui.widget_mut::<Button>(self.cast) {
            w.tooltip_text = tooltip;
        }
        crate::panels::character::set_button_face(
            ui,
            self.cast,
            if enabled {
                crate::states::NORMAL
            } else {
                crate::states::GHOSTED
            },
        );
    }

    /// Whether the cast button can cast, and its hover text.
    #[must_use]
    pub fn cast_state(&self, view: &MagicView<'_>) -> (bool, Option<String>) {
        let t = self.active_tab;
        let name_of = |id: u32| {
            view.objects
                .get(id)
                .map(|o| o.appropriate_name().into_owned())
        };
        if self.endowment_selected[t] && self.endowment_item != 0 {
            let Some(e) = view.objects.get(self.endowment_item) else {
                return (false, None);
            };
            let item = e.appropriate_name().into_owned();
            let composed = match view.metadata(self.endowment_spell) {
                Some(m) => format!("{item} ({})", m.name),
                None => item,
            };
            if e.useability
                .unwrap_or(ItemUseable::UNDEF)
                .allows_self_target()
            {
                return (true, Some(format!("USE the {composed}")));
            }
            return match view.selected.filter(|s| *s != 0) {
                None => (
                    false,
                    Some(format!("You must select a target for the {composed}")),
                ),
                Some(target) => {
                    let target = name_of(target).unwrap_or_else(|| composed.clone());
                    (true, Some(format!("USE the {composed} on {target}")))
                }
            };
        }
        if let Some(spell) = self.selected[t] {
            let Some(m) = view.metadata(spell) else {
                return (false, None);
            };
            let name = &m.name;
            let gate = view
                .cast_gate
                .map_or(SpellCastGate::NoTargetNeeded, |g| g(spell));
            return match gate {
                SpellCastGate::NoTargetNeeded => (true, Some(format!("CAST {name}"))),
                SpellCastGate::TargetCompatible => {
                    let target = view.selected.filter(|s| *s != 0).and_then(name_of);
                    (
                        true,
                        Some(match target {
                            Some(t) => format!("CAST {name} on {t}"),
                            None => format!("CAST {name}"),
                        }),
                    )
                }
                SpellCastGate::TargetIncompatible => (
                    false,
                    Some(format!("You must select an appropriate target for {name}")),
                ),
                SpellCastGate::NoTargetSelected => {
                    (false, Some(format!("You must select a target for {name}")))
                }
                SpellCastGate::Unknown => (false, None),
            };
        }
        let any = self.favorites.iter().any(|f| !f.is_empty());
        (
            false,
            Some(
                if any {
                    "Select a spell to cast"
                } else {
                    "You have no spells ready to cast"
                }
                .to_owned(),
            ),
        )
    }

    fn tab_of_list(&self, list: ElementId) -> Option<usize> {
        self.lists.iter().position(|l| *l == Some(list))
    }

    fn drop_on(&mut self, ui: &mut UiRoot, tab: usize, payload: &DragPayload, position: usize) {
        if let Some(f) = payload.get::<SpellFavoritePayload>() {
            gameplay(
                ui,
                GameplayCommand::AddFavorite {
                    tab,
                    position,
                    spell: f.spell_id.to_string(),
                },
            );
            self.selected[tab] = Some(f.spell_id);
        } else if let Some(s) = payload.get::<SpellbookShortcutPayload>() {
            gameplay(
                ui,
                GameplayCommand::AddFavorite {
                    tab,
                    position,
                    spell: s.spell_id.to_string(),
                },
            );
            self.selected[tab] = Some(s.spell_id);
        }
        self.shown_revision = None;
    }

    /// Where a dragged favourite lands when dropped on cell `index` of bar
    /// `tab`: an empty cell appends; a later cell of the bar it left counts
    /// it as already gone.
    #[must_use]
    pub fn favorite_drop_index(
        &self,
        payload: &SpellFavoritePayload,
        tab: usize,
        index: usize,
        cell_is_empty: bool,
    ) -> usize {
        if cell_is_empty {
            return index.min(self.favorites[tab].len());
        }
        if payload.source_tab == tab && payload.source_position < index {
            index - 1
        } else {
            index
        }
    }
}

impl PanelController for SpellcastingController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if let Some(i) = self.tabs.iter().position(|t| *t == element) {
            if clicked {
                self.select_tab(ui, i);
            }
            return true;
        }
        if element == self.cast {
            if clicked {
                self.cast_selected(ui);
            }
            return true;
        }
        if element == self.endowment_slot {
            match message {
                ElementMessage::Clicked { .. } => self.select_endowment(ui),
                ElementMessage::DoubleClicked => {
                    self.select_endowment(ui);
                    self.cast_selected(ui);
                }
                _ => {}
            }
            return true;
        }
        // A drop on a bar's own background (past the cells).
        if let Some(tab) = self.tab_of_list(element) {
            // Only a spell from the spellbook lands there; a favourite
            // released there stays off its bar.
            if let ElementMessage::CatalogDropped { payload, x, .. } = message
                && payload.get::<SpellbookShortcutPayload>().is_some()
            {
                let count = self.favorites[tab].len();
                let position = ((*x).max(0) as f32 / FAVORITE_CELL_PX).floor() as usize;
                self.drop_on(ui, tab, payload, position.min(count));
            }
            return true;
        }
        let Some(list) = ui.parent(element) else {
            return false;
        };
        let Some(tab) = self.tab_of_list(list) else {
            return false;
        };
        let (id, empty) = ui
            .widget::<CatalogSlot>(element)
            .map_or((0, true), |s| (s.entry_id, s.entry_id == 0));
        match message {
            ElementMessage::Pressed if id != 0 => self.select_spell(ui, id),
            ElementMessage::DoubleClicked if id != 0 => self.cast_selected(ui),
            ElementMessage::RightClicked if id != 0 => emit(ui, PanelOutput::ExamineSpell(id)),
            ElementMessage::CatalogDragLifted(p) => {
                if let Some(f) = p.get::<SpellFavoritePayload>() {
                    // The favourite leaves its bar as it is lifted; the
                    // bar's count drops at once (a drop on the empty tail
                    // lands after the others), and the bar is rebuilt from
                    // the book once the drag is over.
                    self.favorites[f.source_tab].retain(|s| *s != f.spell_id);
                    self.shown_revision = None;
                    gameplay(
                        ui,
                        GameplayCommand::RemoveFavorite {
                            tab: f.source_tab,
                            spell: f.spell_id.to_string(),
                        },
                    );
                }
            }
            ElementMessage::CatalogDropped { payload, .. } => {
                let index = list_cells(ui, list)
                    .iter()
                    .position(|c| *c == element)
                    .unwrap_or(0);
                let position = match payload.get::<SpellFavoritePayload>() {
                    Some(f) => self.favorite_drop_index(f, tab, index, empty),
                    None => index.min(self.favorites[tab].len()),
                };
                self.drop_on(ui, tab, payload, position);
            }
            _ => {}
        }
        true
    }
}
