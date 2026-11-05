//! The spellbook window and the spell bars.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/SpellbookWindowControllerTests.cs`,
//! `SpellcastingUiControllerTests.cs`, `SpellcastingShortcutInputTests.cs`
//! and `SpellbookRowStyleTests.cs`, on synthetic layouts shaped like the
//! retail spellbook 0x21000034 and combat window 0x21000073 (the real ones
//! are imported in `real_dats`). OpenAC's `addFavorite`, `removeFavorite`,
//! `sendFilter`, `removeSpell`, `examineSpell`, `useItem` and confirmation
//! delegates are [`PanelOutput`]s here; the tests apply what the runtime
//! would to the spellbook and update again.

mod common;

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::{EquipMask, ItemType, ItemUseable};
use ac_core::spells::{SpellTable, Spellbook};
use ac_runtime::gameplay::GameplayCommand;
use ac_runtime::gameplay::magic::SpellCastGate;
use ac_runtime::session::SessionCommand;
use ac_ui::draw::{Color, TextureRef};
use ac_ui::element_info::{ElementInfo, ImageMedia};
use ac_ui::event::{DragAcceptance, DragPayload, kind};
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::magic::{
    self, MagicView, SpellFavoritePayload, SpellKey, SpellbookPage, SpellbookRowStyle,
    SpellbookShortcutPayload, SpellbookWindowController, SpellcastingController,
};
use ac_ui::widgets::{
    Button, CatalogSlot, ItemList, ScrollSource, Scrollbar, ShortcutDigits, Text,
};
use ac_ui::{ElementId, MouseButton};
use common::items::*;
use common::*;

/// OpenAC's pinned spellbook row (SpellbookWindowControllerTests.RowStyle).
const ROW_STYLE: SpellbookRowStyle = SpellbookRowStyle {
    width: 280.0,
    height: 32.0,
    icon_left: 0.0,
    icon_top: 0.0,
    icon_width: 32.0,
    icon_height: 32.0,
    label_left: 42.0,
    label_width: 230.0,
    font_did: 0x4000_0001,
    label_color: Color::WHITE,
    background_sprite: 0x0600_1396,
    selected_sprite: 0x0600_1397,
};

fn button_info(id: u32) -> Arc<ElementInfo> {
    let mut info = ElementInfo {
        id,
        kind: 1,
        width: 32.0,
        height: 16.0,
        default_state_name: "Normal".to_owned(),
        ..ElementInfo::default()
    };
    for (name, file) in [("Normal", 1), ("Highlight", 2), ("Ghosted", 3)] {
        info.state_media
            .insert(name.to_owned(), ImageMedia { file, draw_mode: 1 });
    }
    Arc::new(info)
}

fn add_button(
    ui: &mut ac_ui::root::UiRoot,
    parent: ElementId,
    id: u32,
    x: f32,
    y: f32,
) -> ElementId {
    ui.add(
        parent,
        at(x, y, 32.0, 16.0),
        Button::new(button_info(id), None, Vec::new(), 0.0),
    )
}

fn outputs(host: &mut UiHost) -> Vec<PanelOutput> {
    panel_outputs(host)
}

fn gameplay(outputs: &[PanelOutput]) -> Vec<GameplayCommand> {
    sessions(outputs)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Gameplay(g) => Some(g),
            _ => None,
        })
        .collect()
}

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

// ── The spellbook window ────────────────────────────────────────────────

/// OpenAC's CreateSpellbook: three spells, the creature one sorted first.
fn spell_csv() -> &'static str {
    "Spell ID,Name,School,IconId [Hex],SortKey\n\
     101,War One,War Magic,0x06000001,2\n\
     102,Creature One,Creature Enchantment,0x06000002,1\n\
     103,War Eight,War Magic,0x06000003,3\n"
}

fn spellbook(learned: &[u32]) -> Spellbook {
    let mut b = Spellbook::new(Some(Arc::new(SpellTable::load_from_csv(spell_csv()))));
    for id in learned {
        b.on_spell_learned(*id, 1.0);
    }
    b
}

fn level(id: u32) -> i32 {
    if id == 103 { 8 } else { 1 }
}

struct Book {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    icons: TestIcons,
    objects: Objects,
}

fn book_window() -> Book {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 300.0, 500.0));
    let mut ids = HashMap::new();
    let spells = panel(ui, root, at(0.0, 40.0, 300.0, 440.0));
    ids.insert(magic::SPELL_PAGE_ID, spells);
    ids.insert(
        magic::COMPONENT_PAGE_ID,
        panel(ui, root, at(0.0, 40.0, 300.0, 440.0)),
    );
    for (i, id) in [
        magic::SPELL_TAB_ID,
        magic::COMPONENT_TAB_ID,
        magic::CLOSE_ID,
    ]
    .into_iter()
    .enumerate()
    {
        ids.insert(id, add_button(ui, root, id, i as f32 * 40.0, 0.0));
    }
    for (i, (id, _)) in magic::FILTER_BUTTONS.iter().enumerate() {
        ids.insert(*id, add_button(ui, spells, *id, i as f32 * 20.0, 0.0));
    }
    ids.insert(
        magic::DELETE_BUTTON_ID,
        add_button(ui, spells, magic::DELETE_BUTTON_ID, 0.0, 20.0),
    );
    ids.insert(
        magic::SPELL_LIST_ID,
        list(ui, spells, 0.0, 60.0, 280.0, 160.0),
    );
    ids.insert(
        magic::SPELL_SCROLLBAR_ID,
        ui.add(spells, at(282.0, 60.0, 16.0, 160.0), Scrollbar::default()),
    );
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window("spellbook", root, None, 0).unwrap();
    let c = SpellbookWindowController::bind(ui, &layout, ROW_STYLE, None).expect("binds");
    host.attach_controller("spellbook", Box::new(c)).unwrap();
    Book {
        host,
        ids,
        icons: TestIcons::default(),
        objects: Objects::new(),
    }
}

impl Book {
    fn update(&mut self, book: &Spellbook) {
        let level_of: &dyn Fn(u32) -> i32 = &level;
        let mut view = MagicView::new(book, &self.objects.t, PLAYER);
        view.spell_level = Some(level_of);
        let icons = &mut self.icons;
        self.host
            .with_controller::<SpellbookWindowController, _>("spellbook", |c, ui| {
                c.update(ui, &view, icons);
            })
            .unwrap();
    }

    fn with<R>(&mut self, f: impl FnOnce(&mut SpellbookWindowController) -> R) -> R {
        self.host
            .with_controller::<SpellbookWindowController, _>("spellbook", |c, _| f(c))
            .unwrap()
    }

    fn list(&self) -> &ItemList {
        self.host
            .root
            .widget::<ItemList>(self.ids[&magic::SPELL_LIST_ID])
            .unwrap()
    }

    fn row(&self, i: usize) -> ElementId {
        self.list().item(i).unwrap()
    }

    fn slot(&self, i: usize) -> &CatalogSlot {
        self.host.root.widget::<CatalogSlot>(self.row(i)).unwrap()
    }

    fn rows(&self) -> Vec<u32> {
        (0..self.list().num_items())
            .map(|i| self.slot(i).entry_id)
            .collect()
    }

    fn fire(&mut self, target: ElementId, k: i32, payload: Option<DragPayload>) {
        self.host.root.fire_event(k, target, payload);
        self.host.pump();
    }

    fn click(&mut self, id: u32) {
        let b = self.ids[&id];
        self.fire(b, kind::CLICK, None);
    }

    fn button(&self, id: u32) -> &Button {
        self.host.root.widget::<Button>(self.ids[&id]).unwrap()
    }

    fn visible(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().visible
    }
}

// RetailFixture_BindsTextTabs_AndPropagatesOpenClosedState (the tab
// switching; the authored captions are checked on the real dats).
#[test]
fn the_tabs_switch_between_the_spell_and_component_pages() {
    let mut b = book_window();
    assert_eq!(b.with(|c| c.page()), SpellbookPage::Spells);
    assert!(b.visible(magic::SPELL_PAGE_ID));
    assert!(!b.visible(magic::COMPONENT_PAGE_ID));
    assert!(b.button(magic::SPELL_TAB_ID).selected());
    assert!(!b.button(magic::COMPONENT_TAB_ID).selected());
    b.click(magic::COMPONENT_TAB_ID);
    assert_eq!(b.with(|c| c.page()), SpellbookPage::Components);
    assert!(!b.visible(magic::SPELL_PAGE_ID));
    assert!(b.visible(magic::COMPONENT_PAGE_ID));
    assert!(!b.button(magic::SPELL_TAB_ID).selected());
    assert!(b.button(magic::COMPONENT_TAB_ID).selected());
    assert!(outputs(&mut b.host).is_empty(), "no close");
    b.click(magic::CLOSE_ID);
    assert_dbg_eq!(
        outputs(&mut b.host),
        [PanelOutput::ClosePanel(ac_ui::panels::panel_id::MAGIC)]
    );
}

// RightClickingASpellRow_AssessesIt.
#[test]
fn right_clicking_a_spell_row_examines_it() {
    let mut b = book_window();
    let book = spellbook(&[101]);
    b.update(&book);
    let row = b.row(0);
    b.fire(row, kind::RIGHT_CLICK, None);
    assert_dbg_eq!(outputs(&mut b.host), [PanelOutput::ExamineSpell(101)]);
}

// LearnedSpells_AreAuthoredRows_InDisplayOrder_WithSelectionAndDragPayload.
#[test]
fn learned_spells_are_authored_rows_in_display_order_with_selection_and_drag_payload() {
    let mut b = book_window();
    let book = spellbook(&[101, 102, 103]);
    b.update(&book);
    let list = b.ids[&magic::SPELL_LIST_ID];
    assert_eq!(b.list().columns, 1);
    assert_eq!(b.list().cell_width, 280.0);
    assert_eq!(b.list().cell_height, 32.0);
    let bar = b
        .host
        .root
        .widget::<Scrollbar>(b.ids[&magic::SPELL_SCROLLBAR_ID])
        .unwrap();
    assert!(matches!(bar.model, ScrollSource::Element(l) if l == list));
    assert_eq!(b.rows(), [102, 101, 103]);
    let first = b.slot(0);
    assert_eq!(first.label, "Creature One");
    assert!(first.show_label);
    assert_eq!(first.background_sprite, ROW_STYLE.background_sprite);
    assert_eq!(first.selected_sprite, ROW_STYLE.selected_sprite);
    assert!(first.selection_behind_content);
    assert_eq!(first.label_left, ROW_STYLE.label_left);
    assert_eq!(first.icon, Some(TextureRef::Runtime(0x2000_0000 | 102)));

    let (r0, r1) = (b.row(0), b.row(1));
    b.fire(r1, kind::CLICK, None);
    assert!(b.slot(1).selected);
    assert!(!b.slot(0).selected);
    assert_eq!(b.with(|c| c.selected()), Some(101));

    let p = DragPayload::new(SpellbookShortcutPayload { spell_id: 102 });
    b.fire(r0, kind::DRAG_BEGIN, Some(p));
    assert!(b.slot(0).selected);
    assert_eq!(
        b.slot(0)
            .drag_payload
            .as_ref()
            .and_then(|p| p.get::<SpellbookShortcutPayload>().copied()),
        Some(SpellbookShortcutPayload { spell_id: 102 })
    );

    b.fire(r0, kind::DOUBLE_CLICK, None);
    assert_dbg_eq!(outputs(&mut b.host), [PanelOutput::FavoriteSpell(102)]);
}

// SchoolAndLevelFilters_RebuildFromAuthoritativeBitfield.
#[test]
fn school_and_level_filters_rebuild_from_the_book_bitfield() {
    let mut b = book_window();
    let mut book = spellbook(&[101, 102, 103]);
    b.update(&book);
    assert!(b.button(0x1000_029B).selected(), "war on");
    assert_eq!(b.rows().len(), 3);

    b.click(0x1000_029B);
    assert_dbg_eq!(
        gameplay(&outputs(&mut b.host)),
        [GameplayCommand::SetSpellbookFilter(0x3FF7)]
    );
    book.set_spellbook_filters(0x3FF7);
    b.update(&book);
    assert_eq!(b.rows(), [102]);

    // Two toggles before the book answers both count.
    b.click(0x1000_029B);
    b.click(0x1000_029C);
    assert_dbg_eq!(
        gameplay(&outputs(&mut b.host)),
        [
            GameplayCommand::SetSpellbookFilter(0x3FFF),
            GameplayCommand::SetSpellbookFilter(0x3FEF)
        ]
    );
    book.set_spellbook_filters(0x3FEF);
    b.update(&book);
    assert_eq!(b.rows(), [103]);
    assert!(!b.button(0x1000_029C).selected(), "level one off");
}

// Delete_UsesSharedConfirmation_AndWaitsForServerRemovalNotice.
#[test]
fn delete_asks_for_confirmation_and_waits_for_the_server_to_forget() {
    let mut b = book_window();
    let mut book = spellbook(&[101]);
    b.update(&book);
    b.click(magic::DELETE_BUTTON_ID);
    assert!(outputs(&mut b.host).is_empty(), "nothing selected");

    let row = b.row(0);
    b.fire(row, kind::CLICK, None);
    b.click(magic::DELETE_BUTTON_ID);
    let out = outputs(&mut b.host);
    let [PanelOutput::Confirm { message, on_yes }] = &out[..] else {
        panic!("a confirmation: {out:?}");
    };
    assert_eq!(
        message,
        "Are you sure you want to remove War One from your spellbook? \
         You will no longer be able to cast this spell unless you learn it again!"
    );
    assert_dbg_eq!(
        **on_yes,
        PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::ForgetSpell(
            "101".into()
        )))
    );
    // The row stays until the server's removal notice.
    b.update(&book);
    assert_eq!(b.rows(), [101]);
    book.on_spell_forgotten(101);
    b.update(&book);
    assert!(b.rows().is_empty());
}

// SpellbookRowStyleTests.InstalledDat_ResolvesPinnedRetailSpellShortcutPrototype
// (from the prototype's description; the dat lookup runs in `real_dats`).
#[test]
fn the_row_style_reads_the_prototype() {
    let mut proto = ElementInfo {
        id: SpellbookRowStyle::PROTOTYPE_ID,
        width: 280.0,
        height: 32.0,
        ..ElementInfo::default()
    };
    proto.state_media.insert(
        String::new(),
        ImageMedia {
            file: 0x0600_1396,
            draw_mode: 1,
        },
    );
    let mut selected = ElementInfo {
        id: SpellbookRowStyle::SELECTED_OVERLAY_ID,
        ..ElementInfo::default()
    };
    selected.state_media.insert(
        String::new(),
        ImageMedia {
            file: 0x0600_1397,
            draw_mode: 1,
        },
    );
    let label = ElementInfo {
        id: SpellbookRowStyle::LABEL_ID,
        x: 42.0,
        width: 230.0,
        height: 32.0,
        font_did: 0x4000_0001,
        ..ElementInfo::default()
    };
    let icon = ElementInfo {
        id: SpellbookRowStyle::ICON_ID,
        width: 32.0,
        height: 32.0,
        ..ElementInfo::default()
    };
    proto.children = vec![selected, label, icon];
    assert_eq!(SpellbookRowStyle::from_info(&proto), Some(ROW_STYLE));
    proto.children.pop();
    assert_eq!(SpellbookRowStyle::from_info(&proto), None, "no icon");
}

#[test]
fn filters_need_both_the_school_and_the_level_bit() {
    use ac_core::spells::MagicSchool;
    assert_eq!(magic::school_filter_bit(MagicSchool::VOID_MAGIC), 0x2000);
    assert!(magic::spell_visible(MagicSchool::WAR_MAGIC, 1, 0x18));
    assert!(!magic::spell_visible(MagicSchool::WAR_MAGIC, 2, 0x18));
    assert!(!magic::spell_visible(MagicSchool::LIFE_MAGIC, 1, 0x18));
    assert!(magic::spell_visible(MagicSchool::LIFE_MAGIC, 8, 0x804));
    assert!(!magic::spell_visible(MagicSchool::LIFE_MAGIC, 0, 0x3FFF));
    assert!(!magic::spell_visible(MagicSchool::LIFE_MAGIC, 9, 0x3FFF));
}

// ── The spell bars ──────────────────────────────────────────────────────

const ORB: u32 = 0x5000_0002;

struct Bars {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    icons: TestIcons,
    objects: Objects,
    selected: Option<u32>,
    gate: SpellCastGate,
}

/// The combat window's magic page: the eight tabs at y = 0, the groups at
/// y = 40 each with a 576-pixel bar (18 cells) and its scrollbar, the cast
/// button, the endowment cell and the spell name.
fn bars(digits: Option<Arc<ShortcutDigits>>, empty_sprite: u32) -> Bars {
    let mut host = UiHost::new();
    host.root.set_size(1000.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 900.0, 200.0));
    let mut ids = HashMap::new();
    for i in 0..8 {
        let tab = magic::FAVORITE_TAB_IDS[i];
        ids.insert(tab, add_button(ui, root, tab, i as f32 * 40.0, 0.0));
        let g = panel(ui, root, at(0.0, 40.0, 620.0, 52.0));
        ids.insert(magic::FAVORITE_GROUP_IDS[i], g);
        list(ui, g, 0.0, 0.0, 576.0, 32.0);
        let bar = ui.add(g, at(0.0, 34.0, 576.0, 16.0), Scrollbar::default());
        ui.element_mut(bar).unwrap().dat_element_id = magic::FAVORITE_SCROLLBAR_ID;
    }
    ids.insert(
        magic::CAST_BUTTON_ID,
        add_button(ui, root, magic::CAST_BUTTON_ID, 662.0, 40.0),
    );
    ids.insert(
        magic::ENDOWMENT_ID,
        panel(ui, root, at(720.0, 40.0, 32.0, 32.0)),
    );
    ids.insert(
        magic::SPELL_NAME_ID,
        ui.add(root, at(0.0, 100.0, 576.0, 16.0), Text::default()),
    );
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window("combat", root, None, 0).unwrap();
    let c = SpellcastingController::bind(ui, &layout, digits, empty_sprite).expect("binds");
    host.attach_controller("combat", Box::new(c)).unwrap();
    let mut objects = Objects::new();
    objects.player(102, 7);
    Bars {
        host,
        ids,
        icons: TestIcons::default(),
        objects,
        selected: None,
        gate: SpellCastGate::NoTargetNeeded,
    }
}

fn favorites(spells: &[u32]) -> Spellbook {
    let mut b = Spellbook::default();
    for (i, id) in spells.iter().enumerate() {
        b.on_spell_learned(*id, 1.0);
        b.set_favorite(0, i, *id);
    }
    b
}

impl Bars {
    fn update(&mut self, book: &Spellbook) {
        let g = self.gate;
        let gate: &dyn Fn(u32) -> SpellCastGate = &move |_| g;
        let mut view = MagicView::new(book, &self.objects.t, PLAYER);
        view.selected = self.selected;
        view.cast_gate = Some(gate);
        let icons = &mut self.icons;
        self.host
            .with_controller::<SpellcastingController, _>("combat", |c, ui| {
                c.update(ui, &view, icons);
            })
            .unwrap();
    }

    fn with<R>(
        &mut self,
        f: impl FnOnce(&mut SpellcastingController, &mut ac_ui::root::UiRoot) -> R,
    ) -> R {
        self.host
            .with_controller::<SpellcastingController, _>("combat", f)
            .unwrap()
    }

    fn list_id(&mut self, tab: usize) -> ElementId {
        self.with(|c, _| c.list(tab)).unwrap()
    }

    fn list(&mut self) -> &ItemList {
        let l = self.list_id(0);
        self.host.root.widget::<ItemList>(l).unwrap()
    }

    fn cell(&mut self, i: usize) -> ElementId {
        self.list().item(i).unwrap()
    }

    fn slot(&mut self, i: usize) -> &CatalogSlot {
        let c = self.cell(i);
        self.host.root.widget::<CatalogSlot>(c).unwrap()
    }

    fn entries(&mut self) -> Vec<u32> {
        let n = self.list().num_items();
        (0..n).map(|i| self.slot(i).entry_id).collect()
    }

    fn fire(&mut self, target: ElementId, k: i32, payload: Option<DragPayload>) {
        self.host.root.fire_event(k, target, payload);
        self.host.pump();
    }

    fn click(&mut self, id: u32) {
        let b = self.ids[&id];
        self.fire(b, kind::CLICK, None);
    }

    fn endowment(&mut self) -> ElementId {
        self.with(|c, _| c.endowment_slot())
    }

    fn cast(&self) -> (bool, Option<String>) {
        let b = self.ids[&magic::CAST_BUTTON_ID];
        (
            self.host.root.element(b).unwrap().enabled,
            self.host
                .root
                .widget::<Button>(b)
                .unwrap()
                .tooltip_text
                .clone(),
        )
    }

    /// The centre of cell `i` of the open bar, on screen.
    fn centre(i: usize) -> (i32, i32) {
        (i as i32 * 32 + 16, 40 + 16)
    }

    fn accept_rings(&mut self) -> Vec<usize> {
        let n = self.list().num_items();
        (0..n)
            .filter(|i| self.slot(*i).drag_acceptance() == DragAcceptance::Accept)
            .collect()
    }

    fn orb(&mut self, useability: Option<ItemUseable>) {
        self.objects.equipped(ORB, EquipMask::HELD);
        let o = self.objects.get_mut(ORB);
        o.name = "Orb".into();
        o.item_type = ItemType::CASTER;
        o.spell_id = Some(2670);
        o.useability = useability;
    }
}

fn digit_set() -> Arc<ShortcutDigits> {
    Arc::new(ShortcutDigits {
        regular: (101..=109).collect(),
        ghosted: (201..=209).collect(),
        empty: Some((301..=309).collect()),
    })
}

// ImportedFixture_UsesRetailEmptyCells_ShortcutDigits_AndCompleteCastButton
// (the bar; the authored arrows and cast button face run on the real dats).
#[test]
fn a_bar_fills_with_empty_cells_and_numbers_the_first_nine() {
    let mut b = bars(Some(digit_set()), 0x0600_1A97);
    let book = favorites(&[42]);
    b.update(&book);
    let list_id = b.list_id(0);
    let l = b.list();
    assert!(l.single_row && l.horizontal_scroll && l.fill_visible_empty_slots);
    assert_eq!(l.num_items(), 18);
    let g = b.ids[&magic::FAVORITE_GROUP_IDS[0]];
    let bar = b.host.root.children(g)[1];
    let s = b.host.root.widget::<Scrollbar>(bar).unwrap();
    assert!(s.horizontal);
    assert!(matches!(s.model, ScrollSource::Element(l) if l == list_id));

    let first = b.slot(0);
    assert_eq!(first.entry_id, 42);
    assert_eq!(first.shortcut_num, 0);
    assert_eq!(
        first.active_digits(),
        Some(&(101..=109).collect::<Vec<_>>()[..])
    );
    for i in 1..9 {
        let s = b.slot(i);
        assert_eq!(s.entry_id, 0);
        assert_eq!(s.empty_sprite, 0x0600_1A97);
        assert_eq!(s.shortcut_num, i as i32);
        assert_eq!(
            s.active_digits(),
            Some(&(301..=309).collect::<Vec<_>>()[..])
        );
    }
    for i in 9..18 {
        assert_eq!(b.slot(i).empty_sprite, 0x0600_1A97);
        assert_eq!(b.slot(i).shortcut_num, -1);
    }
}

// FavoriteOverflow_ArrowButtonsStepOneCell_AndSelectionScrollsIntoView.
#[test]
fn an_overflowing_bar_scrolls_to_the_selection_and_keeps_a_manual_offset() {
    let mut b = bars(None, 0);
    let ids: Vec<u32> = (1..=20).collect();
    let book = favorites(&ids);
    b.update(&book);
    assert_eq!(b.list().num_items(), 20);
    assert_eq!(b.list().scroll.max_scroll(), 64);
    b.with(|c, ui| c.add_favorite(ui, 20));
    assert!(
        gameplay(&outputs(&mut b.host)).is_empty(),
        "already on the bar"
    );
    assert_eq!(b.list().scroll.scroll_y(), 64);
    assert_eq!(b.with(|c, _| c.selected_spell()), Some(20));
    let l = b.list_id(0);
    b.host
        .root
        .widget_mut::<ItemList>(l)
        .unwrap()
        .scroll
        .set_scroll_y(32);
    b.objects.contained(0x7000_0099, PLAYER, 0, ItemType::MISC);
    b.update(&book);
    assert_eq!(b.list().scroll.scroll_y(), 32);
}

// ImportedFixture_BindsAllTabs_AndTracksEquippedEndowment.
#[test]
fn the_wielded_caster_shows_as_the_endowment_and_cast_uses_it() {
    let mut b = bars(None, 0);
    let book = Spellbook::default();
    b.update(&book);
    let host_id = b.ids[&magic::ENDOWMENT_ID];
    assert!(!b.host.root.element(host_id).unwrap().visible);
    b.orb(Some(ItemUseable(
        (ItemUseable::SELF.0 << 16) | ItemUseable::WIELDED.0,
    )));
    b.update(&book);
    assert!(b.host.root.element(host_id).unwrap().visible);
    let e = b.endowment();
    let s = b.host.root.widget::<CatalogSlot>(e).unwrap();
    assert_eq!(s.entry_id, ORB);
    assert_eq!(s.icon, Some(TextureRef::Runtime(0x2000_0000 | 2670)));
    assert_eq!(
        s.overlay,
        Some(TextureRef::Runtime(0x1000_0000 | 0x0600_0000 | ORB))
    );
    b.fire(e, kind::CLICK, None);
    b.update(&book);
    b.click(magic::CAST_BUTTON_ID);
    assert_eq!(uses(&outputs(&mut b.host)), [ORB]);
}

// The tabs show one group at a time.
#[test]
fn a_tab_shows_its_own_bar() {
    let mut b = bars(None, 0);
    let book = favorites(&[42]);
    b.update(&book);
    let groups = magic::FAVORITE_GROUP_IDS.map(|g| b.ids[&g]);
    assert!(b.host.root.element(groups[0]).unwrap().visible);
    assert!(!b.host.root.element(groups[3]).unwrap().visible);
    b.click(magic::FAVORITE_TAB_IDS[3]);
    assert_eq!(b.with(|c, _| c.active_tab()), 3);
    assert!(!b.host.root.element(groups[0]).unwrap().visible);
    assert!(b.host.root.element(groups[3]).unwrap().visible);
    assert_eq!(b.with(|c, _| c.selected_spell()), None, "an empty bar");
    b.with(|c, ui| c.handle_key(ui, SpellKey::PrevTab));
    assert_eq!(b.with(|c, _| c.active_tab()), 2);
    b.with(|c, ui| c.handle_key(ui, SpellKey::LastTab));
    assert_eq!(b.with(|c, _| c.active_tab()), 7);
    b.with(|c, ui| c.handle_key(ui, SpellKey::NextTab));
    assert_eq!(b.with(|c, _| c.active_tab()), 0);
    assert_eq!(b.with(|c, _| c.selected_spell()), Some(42));
}

fn drag(b: &mut Bars, from: usize) {
    let (x, y) = Bars::centre(from);
    b.host.mouse_down(MouseButton::Left, x, y, 0);
    b.host.mouse_move(x + 20, y);
    b.host.pump();
}

fn applied(book: &mut Spellbook, commands: &[GameplayCommand]) {
    for c in commands {
        match c {
            GameplayCommand::AddFavorite {
                tab,
                position,
                spell,
            } => book.set_favorite(*tab, *position, spell.parse().unwrap()),
            GameplayCommand::RemoveFavorite { tab, spell } => {
                book.remove_favorite(*tab, spell.parse().unwrap());
            }
            _ => {}
        }
    }
}

// DragFavoriteOntoAnotherSlot_ThroughTheRealPointerPipeline_ReordersAndSyncsWire.
#[test]
fn dragging_a_favourite_onto_another_cell_reorders_the_bar() {
    let mut b = bars(None, 0);
    let mut book = favorites(&[1, 2, 3]);
    b.update(&book);
    let slot0 = b.cell(0);
    drag(&mut b, 0);
    assert_eq!(b.host.root.drag_source(), Some(slot0));
    let lifted = gameplay(&outputs(&mut b.host));
    assert_dbg_eq!(
        lifted,
        [GameplayCommand::RemoveFavorite {
            tab: 0,
            spell: "1".into()
        }]
    );
    applied(&mut book, &lifted);
    b.update(&book);
    assert_eq!(
        b.host.root.drag_source(),
        Some(slot0),
        "no rebuild mid-drag"
    );

    let (x2, y2) = Bars::centre(2);
    b.host.mouse_move(x2, y2);
    b.host.mouse_up(MouseButton::Left, x2, y2, 0);
    b.host.pump();
    assert_eq!(b.host.root.drag_source(), None);
    let dropped = gameplay(&outputs(&mut b.host));
    assert_dbg_eq!(
        dropped,
        [GameplayCommand::AddFavorite {
            tab: 0,
            position: 1,
            spell: "1".into()
        }]
    );
    applied(&mut book, &dropped);
    assert_eq!(book.get_favorites(0), [2, 1, 3]);
    b.update(&book);
    assert_eq!(&b.entries()[..3], [2, 1, 3]);
    assert_eq!(b.with(|c, _| c.selected_spell()), Some(1));
}

// SpellFavoriteDrag_ShowsAuthoredAcceptRing_TracksPointer_AndNeverLiesAboutTheLanding.
#[test]
fn the_accept_ring_follows_the_pointer_and_the_landing_matches_it() {
    let mut b = bars(None, 0);
    let book = favorites(&[1, 2, 3]);
    b.update(&book);
    drag(&mut b, 0);
    let (x2, y2) = Bars::centre(2);
    let (x1, y1) = Bars::centre(1);
    b.host.mouse_move(x2, y2);
    assert_eq!(b.accept_rings(), [2]);
    b.update(&book);
    assert_eq!(b.accept_rings(), [2]);
    b.host.mouse_move(x1, y1);
    assert_eq!(b.accept_rings(), [1]);
    b.host.mouse_move(x2, y2);
    assert_eq!(b.accept_rings(), [2]);
    let payload = *b
        .host
        .root
        .drag_payload()
        .and_then(|p| p.get::<SpellFavoritePayload>())
        .unwrap();
    let promised = b.with(|c, _| c.favorite_drop_index(&payload, 0, 2, false));
    b.host.take_outputs();
    b.host.mouse_up(MouseButton::Left, x2, y2, 0);
    b.host.pump();
    assert_dbg_eq!(
        gameplay(&outputs(&mut b.host)),
        [GameplayCommand::AddFavorite {
            tab: 0,
            position: promised,
            spell: "1".into()
        }]
    );
    assert!(b.accept_rings().is_empty());
}

// SpellFavoriteDrag_DroppedOnTheEmptyTail_AppendsAtTheEnd.
#[test]
fn a_favourite_dropped_on_the_empty_tail_goes_last() {
    let mut b = bars(None, 0);
    let mut book = favorites(&[1, 2, 3]);
    b.update(&book);
    assert_eq!(b.slot(3).entry_id, 0);
    drag(&mut b, 0);
    let (ex, ey) = Bars::centre(3);
    b.host.mouse_move(ex, ey);
    assert_eq!(b.accept_rings(), [3]);
    let payload = *b
        .host
        .root
        .drag_payload()
        .and_then(|p| p.get::<SpellFavoritePayload>())
        .unwrap();
    assert_eq!(
        b.with(|c, _| c.favorite_drop_index(&payload, 0, 3, true)),
        2
    );
    b.host.mouse_up(MouseButton::Left, ex, ey, 0);
    b.host.pump();
    let out = gameplay(&outputs(&mut b.host));
    applied(&mut book, &out);
    assert_dbg_eq!(
        out[1],
        GameplayCommand::AddFavorite {
            tab: 0,
            position: 2,
            spell: "1".into()
        }
    );
    assert_eq!(book.get_favorites(0), [2, 3, 1]);
}

// SpellFavoriteDrag_RingClearsOnLeave_AndAnOffBarReleaseKeepsTheLiftRemoval.
#[test]
fn a_favourite_released_off_the_bar_stays_removed() {
    let mut b = bars(None, 0);
    let mut book = favorites(&[1, 2, 3]);
    b.update(&book);
    drag(&mut b, 0);
    let (x2, y2) = Bars::centre(2);
    b.host.mouse_move(x2, y2);
    assert_eq!(b.accept_rings(), [2]);
    b.host.mouse_move(990, 590);
    assert!(b.accept_rings().is_empty());
    b.host.mouse_up(MouseButton::Left, 990, 590, 0);
    b.host.pump();
    assert_eq!(b.host.root.drag_source(), None);
    let out = gameplay(&outputs(&mut b.host));
    assert_dbg_eq!(
        out,
        [GameplayCommand::RemoveFavorite {
            tab: 0,
            spell: "1".into()
        }]
    );
    applied(&mut book, &out);
    b.update(&book);
    assert_eq!(&b.entries()[..3], [2, 3, 0]);
}

// FavoriteCellRing_AcceptsSpellPayloads_StaysNeutralForPhysicalItems.
#[test]
fn favourite_cells_ring_for_spells_and_stay_neutral_for_items() {
    let mut b = bars(None, 0);
    let book = favorites(&[1, 2, 3]);
    b.update(&book);
    let (occupied, empty) = (b.cell(0), b.cell(3));
    let physical = || Some(DragPayload::new(payload(9)));
    b.fire(occupied, kind::DRAG_ENTER, physical());
    assert_eq!(b.slot(0).drag_acceptance(), DragAcceptance::None);
    b.fire(
        occupied,
        kind::DRAG_ENTER,
        Some(DragPayload::new(SpellbookShortcutPayload { spell_id: 42 })),
    );
    assert_eq!(b.slot(0).drag_acceptance(), DragAcceptance::Accept);
    b.fire(occupied, kind::DRAG_OVER, None);
    assert_eq!(b.slot(0).drag_acceptance(), DragAcceptance::None);
    b.fire(
        empty,
        kind::DRAG_ENTER,
        Some(DragPayload::new(SpellFavoritePayload {
            source_tab: 0,
            source_position: 0,
            spell_id: 1,
        })),
    );
    assert_eq!(b.slot(3).drag_acceptance(), DragAcceptance::Accept);
    b.fire(empty, kind::DROP_RELEASED, physical());
    assert_eq!(b.slot(3).drag_acceptance(), DragAcceptance::None);
}

// FavoriteDrop_IgnoresForeignInventoryPayload.
#[test]
fn an_item_dropped_on_a_favourite_is_ignored() {
    let mut b = bars(None, 0);
    let book = favorites(&[42]);
    b.update(&book);
    let c = b.cell(0);
    b.fire(c, kind::DROP_RELEASED, Some(DragPayload::new(payload(9))));
    assert!(gameplay(&outputs(&mut b.host)).is_empty());
}

// SpellbookShortcutDrop_AddsToOpenTabWithoutRemovingLearnedSpell.
#[test]
fn a_spell_from_the_spellbook_dropped_on_the_bar_is_added_to_the_open_tab() {
    let mut b = bars(None, 0);
    let book = favorites(&[]);
    b.update(&book);
    let l = b.list_id(0);
    b.fire(
        l,
        kind::DROP_RELEASED,
        Some(DragPayload::new(SpellbookShortcutPayload { spell_id: 42 })),
    );
    assert_dbg_eq!(
        gameplay(&outputs(&mut b.host)),
        [GameplayCommand::AddFavorite {
            tab: 0,
            position: 0,
            spell: "42".into()
        }]
    );
    // On an empty cell past the end it lands last.
    let c = b.cell(5);
    b.fire(
        c,
        kind::DROP_RELEASED,
        Some(DragPayload::new(SpellbookShortcutPayload { spell_id: 42 })),
    );
    assert_dbg_eq!(
        gameplay(&outputs(&mut b.host)),
        [GameplayCommand::AddFavorite {
            tab: 0,
            position: 0,
            spell: "42".into()
        }]
    );
}

// UnrelatedObjectUpdate_DoesNotRecreateFavoriteSlots.
#[test]
fn an_unrelated_object_update_keeps_the_favourite_cells() {
    let mut b = bars(None, 0);
    let book = favorites(&[42]);
    b.update(&book);
    let before = b.cell(0);
    b.objects.contained(0x7000_0099, PLAYER, 0, ItemType::MISC);
    b.update(&book);
    assert_eq!(b.cell(0), before);
}

// ObjectTableClear_RemovesEquippedEndowmentAndCannotUseStaleGuid.
#[test]
fn a_gone_caster_leaves_no_endowment_to_use() {
    let mut b = bars(None, 0);
    let book = Spellbook::default();
    b.orb(None);
    b.update(&book);
    let e = b.endowment();
    b.fire(e, kind::CLICK, None);
    b.objects = Objects::new();
    b.objects.player(102, 7);
    b.update(&book);
    assert!(
        !b.host
            .root
            .element(b.ids[&magic::ENDOWMENT_ID])
            .unwrap()
            .visible
    );
    assert_eq!(b.host.root.widget::<CatalogSlot>(e).unwrap().entry_id, 0);
    b.fire(e, kind::DOUBLE_CLICK, None);
    assert!(uses(&outputs(&mut b.host)).is_empty());
}

// EndowmentDoubleClick_ActivatesTheCasterAndNeverCastsFromTheSpellbook.
#[test]
fn double_clicking_the_endowment_uses_the_caster_and_casts_nothing() {
    let mut b = bars(None, 0);
    let book = Spellbook::default();
    b.orb(None);
    b.update(&book);
    let e = b.endowment();
    b.fire(e, kind::DOUBLE_CLICK, None);
    let out = outputs(&mut b.host);
    assert_eq!(uses(&out), [ORB]);
    assert!(gameplay(&out).is_empty());
}

// FavoritePressSelectsImmediately_AndRightClickExaminesLocally.
#[test]
fn a_press_selects_a_favourite_and_a_right_click_examines_it() {
    let mut b = bars(None, 0);
    let book = favorites(&[42, 43]);
    b.update(&book);
    assert!(b.slot(0).selected);
    let second = b.cell(1);
    b.fire(second, kind::MOUSE_DOWN, None);
    assert!(!b.slot(0).selected);
    assert!(b.slot(1).selected);
    b.fire(second, kind::RIGHT_CLICK, None);
    let out = outputs(&mut b.host);
    assert_dbg_eq!(out, [PanelOutput::ExamineSpell(43)]);
    // A double click casts the selection.
    b.fire(second, kind::DOUBLE_CLICK, None);
    assert_dbg_eq!(
        gameplay(&outputs(&mut b.host)),
        [GameplayCommand::Cast {
            spell: "43".into(),
            target: None
        }]
    );
}

fn named_book(id: u32, name: &str, favourite: bool) -> Spellbook {
    let csv = format!("Spell ID,Name,School\n{id},{name},War Magic\n");
    let mut b = Spellbook::new(Some(Arc::new(SpellTable::load_from_csv(&csv))));
    if favourite {
        b.on_spell_learned(id, 1.0);
        b.set_favorite(0, 0, id);
    }
    b
}

// CastAvailability_UntargetedSpell_IsEnabled_WithCastSpellNameTooltip.
#[test]
fn an_untargeted_spell_can_be_cast() {
    let mut b = bars(None, 0);
    let book = named_book(42, "Test Untargeted", true);
    b.update(&book);
    assert_eq!(b.cast(), (true, Some("CAST Test Untargeted".into())));
}

// CastAvailability_TargetedSpell_CompatibleTargetSelected_AppendsOnTargetName.
#[test]
fn a_targeted_spell_names_its_target() {
    let mut b = bars(None, 0);
    let book = named_book(42, "Test Targeted", true);
    b.objects.contained(5, PLAYER, 0, ItemType::MISC);
    b.objects.get_mut(5).name = "Drudge".into();
    b.selected = Some(5);
    b.gate = SpellCastGate::TargetCompatible;
    b.update(&book);
    assert_eq!(
        b.cast(),
        (true, Some("CAST Test Targeted on Drudge".into()))
    );
}

// CastAvailability_TargetedSpell_NoTargetSelected_IsDisabled_WithNeedsTargetTooltip.
#[test]
fn a_targeted_spell_without_a_target_cannot_be_cast() {
    let mut b = bars(None, 0);
    let book = named_book(42, "Test Targeted", true);
    b.gate = SpellCastGate::NoTargetSelected;
    b.update(&book);
    assert_eq!(
        b.cast(),
        (
            false,
            Some("You must select a target for Test Targeted".into())
        )
    );
}

// CastAvailability_TargetedSpell_IncompatibleTargetSelected_IsDisabled_WithNeedsAppropriateTargetTooltip.
#[test]
fn a_targeted_spell_on_the_wrong_target_cannot_be_cast() {
    let mut b = bars(None, 0);
    let book = named_book(42, "Test Targeted", true);
    b.selected = Some(5);
    b.gate = SpellCastGate::TargetIncompatible;
    b.update(&book);
    assert_eq!(
        b.cast(),
        (
            false,
            Some("You must select an appropriate target for Test Targeted".into())
        )
    );
}

// CastAvailability_EndowmentSelfTarget_ComposesItemAndSpellName.
#[test]
fn a_self_targeted_endowment_names_the_caster_and_its_spell() {
    let mut b = bars(None, 0);
    let book = named_book(2670, "Lightning Bolt VI", false);
    b.orb(Some(ItemUseable(ItemUseable::SELF.0 << 16)));
    b.objects.get_mut(ORB).name = "Lightning Wand".into();
    b.update(&book);
    assert_eq!(
        b.cast(),
        (
            true,
            Some("USE the Lightning Wand (Lightning Bolt VI)".into())
        )
    );
}

// CastAvailability_EndowmentNeedsTarget_NoTargetSelected_ComposesItemAndSpellName.
#[test]
fn a_targeted_endowment_without_a_target_cannot_be_used() {
    let mut b = bars(None, 0);
    let book = named_book(2670, "Lightning Bolt VI", false);
    b.orb(Some(ItemUseable(ItemUseable::REMOTE.0 << 16)));
    b.objects.get_mut(ORB).name = "Lightning Wand".into();
    b.update(&book);
    assert_eq!(
        b.cast(),
        (
            false,
            Some("You must select a target for the Lightning Wand (Lightning Bolt VI)".into())
        )
    );
}

#[test]
fn with_nothing_on_the_bars_there_is_nothing_to_cast() {
    let mut b = bars(None, 0);
    b.update(&Spellbook::default());
    assert_eq!(
        b.cast(),
        (false, Some("You have no spells ready to cast".into()))
    );
}

// SpellcastingShortcutInputTests (AllRetailSpellSlotsMapToFavoriteIndex,
// EveryRetailFavoriteSpellSlotHasALiveConsumer).
#[test]
fn spell_slot_keys_cast_the_favourite_in_that_place() {
    for key in 1..=12 {
        assert_eq!(magic::spell_slot_key(key), Some(SpellKey::UseSlot(key - 1)));
    }
    assert_eq!(magic::spell_slot_key(0), None);
    assert_eq!(magic::spell_slot_key(13), None);
    let mut b = bars(None, 0);
    let book = favorites(&[7, 8, 9]);
    b.update(&book);
    b.with(|c, ui| c.handle_key(ui, SpellKey::UseSlot(1)));
    assert_dbg_eq!(
        gameplay(&outputs(&mut b.host)),
        [GameplayCommand::Cast {
            spell: "8".into(),
            target: None
        }]
    );
    assert_eq!(b.with(|c, _| c.selected_spell()), Some(8));
    b.with(|c, ui| c.handle_key(ui, SpellKey::UseSlot(5)));
    assert!(outputs(&mut b.host).is_empty(), "an empty place");
    b.with(|c, ui| c.handle_key(ui, SpellKey::NextSpell));
    assert_eq!(b.with(|c, _| c.selected_spell()), Some(9));
    b.with(|c, ui| c.handle_key(ui, SpellKey::NextSpell));
    assert_eq!(b.with(|c, _| c.selected_spell()), Some(7), "wraps");
    b.with(|c, ui| c.handle_key(ui, SpellKey::LastSpell));
    assert_eq!(b.with(|c, _| c.selected_spell()), Some(9));
    b.with(|c, ui| c.handle_key(ui, SpellKey::CastCurrent));
    assert_dbg_eq!(
        gameplay(&outputs(&mut b.host)),
        [GameplayCommand::Cast {
            spell: "9".into(),
            target: None
        }]
    );
}

#[test]
fn the_selected_spell_name_shows_under_the_bar() {
    let mut b = bars(None, 0);
    let book = named_book(42, "Test Untargeted", true);
    b.update(&book);
    let t = b.ids[&magic::SPELL_NAME_ID];
    let lines = b.host.root.widget::<Text>(t).unwrap().content_lines();
    assert_eq!(lines.len(), 1);
    assert_eq!(lines[0].text, "Test Untargeted");
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

    // SpellbookRowStyleTests.InstalledDat_ResolvesPinnedRetailSpellShortcutPrototype.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_row_style_resolves_from_the_catalog() {
        let c = content();
        assert_eq!(SpellbookRowStyle::load(&c), Some(ROW_STYLE));
    }

    // SpellbookWindowControllerTests on the installed layout.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_spellbook_imports_binds_and_lists_learned_spells() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(
            &mut host.root,
            &c,
            magic::SPELLBOOK_LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("spellbook layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let delete = layout.find(magic::DELETE_BUTTON_ID).unwrap();
        let e = host.root.element(delete).unwrap();
        assert_eq!((e.left, e.top), (187.0, 61.0));
        host.root
            .register_window("spellbook", layout.root, None, 0)
            .unwrap();
        let style = SpellbookRowStyle::load(&c).unwrap();
        let ctl =
            SpellbookWindowController::bind(&mut host.root, &layout, style, None).expect("binds");
        host.attach_controller("spellbook", Box::new(ctl)).unwrap();
        let book = spellbook(&[101, 102, 103]);
        let o = Objects::new();
        let level_of: &dyn Fn(u32) -> i32 = &level;
        let mut view = MagicView::new(&book, &o.t, PLAYER);
        view.spell_level = Some(level_of);
        let mut icons = TestIcons::default();
        host.with_controller::<SpellbookWindowController, _>("spellbook", |w, ui| {
            w.update(ui, &view, &mut icons);
        });
        let list = layout.find(magic::SPELL_LIST_ID).unwrap();
        let l = host.root.widget::<ItemList>(list).unwrap();
        assert_eq!(l.num_items(), 3);
        assert_eq!(l.cell_width, host.root.element(list).unwrap().width);
        let row = l.item(1).unwrap();
        assert_eq!(host.root.widget::<CatalogSlot>(row).unwrap().entry_id, 101);
        let war = layout.find(0x1000_029B).unwrap();
        assert!(host.root.widget::<Button>(war).unwrap().selected());
        host.root.fire_event(kind::CLICK, war, None);
        host.pump();
        let out = panel_outputs(&mut host);
        assert_dbg_eq!(
            gameplay(&out),
            [GameplayCommand::SetSpellbookFilter(0x3FF7)]
        );
        let tab = layout.find(magic::COMPONENT_TAB_ID).unwrap();
        host.root.fire_event(kind::CLICK, tab, None);
        host.pump();
        let page =
            host.with_controller::<SpellbookWindowController, _>("spellbook", |w, _| w.page());
        assert_eq!(page, Some(SpellbookPage::Components));
        let drawn = host.draw((1024.0, 768.0), &c);
        assert!(!drawn.main.is_empty());
        assert!(LayoutSource::layout(&c, magic::SPELLBOOK_LAYOUT_ID).is_some());
        assert!(c.take_errors().is_empty());
    }

    // SpellcastingUiControllerTests.ImportedFixture_* on the installed layout.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_spell_bars_import_bind_fit_and_take_a_dragged_favourite() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1280.0, 800.0);
        let layout = import(
            &mut host.root,
            &c,
            magic::COMBAT_LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("combat layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let width = magic::fit_favorite_slots(
            &mut host.root,
            &layout,
            magic::VISIBLE_FAVORITE_SLOTS,
            magic::FAVORITE_CELL_PX,
        )
        .expect("a favourite list");
        assert!(width > 576.0);
        let cast = layout.find(magic::CAST_BUTTON_ID).unwrap();
        assert_eq!(host.root.element(cast).unwrap().width, 75.0);
        host.root
            .register_window("combat", layout.root, None, 0)
            .unwrap();
        host.root.set_visible(layout.root, true);
        let page = layout.find(magic::SPELLCASTING_PAGE_ID).unwrap();
        host.root.set_visible(page, true);
        let ctl =
            SpellcastingController::bind(&mut host.root, &layout, Some(digit_set()), 0x0600_1A97)
                .expect("binds");
        host.attach_controller("combat", Box::new(ctl)).unwrap();
        let mut book = favorites(&[1, 2, 3]);
        let mut o = Objects::new();
        o.player(102, 7);
        let mut icons = TestIcons::default();
        let mut update = |host: &mut UiHost, book: &Spellbook| {
            let view = MagicView::new(book, &o.t, PLAYER);
            host.with_controller::<SpellcastingController, _>("combat", |w, ui| {
                w.update(ui, &view, &mut icons);
            });
        };
        update(&mut host, &book);
        let list = host
            .with_controller::<SpellcastingController, _>("combat", |w, _| w.list(0))
            .flatten()
            .unwrap();
        let l = host.root.widget::<ItemList>(list).unwrap();
        assert_eq!(host.root.element(list).unwrap().width, 576.0);
        assert_eq!(l.num_items(), 18);
        let cells: Vec<ElementId> = l.cells().to_vec();
        let s = host.root.widget::<CatalogSlot>(cells[4]).unwrap();
        assert_eq!((s.entry_id, s.shortcut_num), (0, 4));

        // Drag the first favourite onto the third.
        let (x0, y0) = centre_of(&host.root, cells[0]);
        let (x2, y2) = centre_of(&host.root, cells[2]);
        host.take_outputs();
        host.mouse_down(MouseButton::Left, x0, y0, 0);
        host.mouse_move(x0 + 20, y0);
        host.pump();
        host.mouse_move(x2, y2);
        host.mouse_up(MouseButton::Left, x2, y2, 0);
        host.pump();
        let out = gameplay(&panel_outputs(&mut host));
        assert_dbg_eq!(
            out,
            [
                GameplayCommand::RemoveFavorite {
                    tab: 0,
                    spell: "1".into()
                },
                GameplayCommand::AddFavorite {
                    tab: 0,
                    position: 1,
                    spell: "1".into()
                }
            ]
        );
        applied(&mut book, &out);
        update(&mut host, &book);
        assert_eq!(book.get_favorites(0), [2, 1, 3]);
        let drawn = host.draw((1280.0, 800.0), &c);
        assert!(!drawn.main.is_empty());
        assert!(c.take_errors().is_empty());
    }

    fn centre_of(ui: &ac_ui::root::UiRoot, e: ElementId) -> (i32, i32) {
        let (x, y) = ui.screen_position(e);
        let el = ui.element(e).unwrap();
        ((x + el.width / 2.0) as i32, (y + el.height / 2.0) as i32)
    }
}
