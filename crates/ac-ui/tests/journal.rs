//! The journal's notes page and page list.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/JournalNotesPageControllerTests.cs`,
//! `JournalPageListControllerTests.cs`, `JournalPanelLiveBindTests.cs` and
//! `JournalPanelButtonsAreClickableTests.cs` (the real-dat module). OpenAC's
//! pages call the journal state directly; here they ask with
//! `JournalCommand`s, which the harness carries out on the runtime's own
//! `JournalState` (as the session's communication owner does) and feeds
//! back.

mod common;

use std::collections::HashMap;

use ac_core::journal::JournalPage;
use ac_runtime::communication::{JournalCommand, JournalState};
use ac_runtime::session::SessionCommand;
use ac_ui::ElementId;
use ac_ui::draw::Color;
use ac_ui::element_info::{ElementInfo, TemplateEntry};
use ac_ui::event::kind;
use ac_ui::host::UiHost;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::journal_notes::{self as notes, JournalNotesPageController};
use ac_ui::panels::journal_pages::{self as pages, JournalPageListController, page_contains};
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::{Button, DatElement, Field, ListBox, Text};
use common::items::panel_outputs;
use common::panels::*;
use common::*;

/// 2026-08-21 12:00:00 UTC.
const NOW: i64 = 1_787_313_600_000;
const OUTDOOR_CELL: u32 = 0xA9B4_001F;

/// Carry out a journal command as the communication owner does.
fn apply(state: &mut JournalState, command: &JournalCommand, now: i64) {
    match command {
        JournalCommand::NewPage => state.new_page(),
        JournalCommand::DeletePage(p) => {
            state.delete_page(*p);
        }
        JournalCommand::GotoPage(p) => {
            state.goto_page(*p);
        }
        JournalCommand::Edit {
            label,
            title,
            notes,
        } => state.update_current(label, title, notes),
        JournalCommand::RecordLocation { x, y } => state.record_location(*x, *y),
        JournalCommand::SetTimer {
            days,
            hours,
            minutes,
        } => state.set_timer(*days, *hours, *minutes),
        JournalCommand::StartTimer => {
            state.start_timer(now);
        }
        JournalCommand::ResetTimer => state.reset_timer(),
    }
}

fn titled(title: &str) -> JournalPage {
    JournalPage {
        title: title.into(),
        ..JournalPage::default()
    }
}

// ── The notes page ──────────────────────────────────────────────────────

struct Notes {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    state: JournalState,
    now: i64,
}

fn add_field(ui: &mut UiRoot, ids: &mut HashMap<u32, ElementId>, parent: ElementId, id: u32) {
    let mut e = Element::at(0.0, 0.0, 100.0, 18.0);
    e.dat_element_id = id;
    Field::configure(&mut e);
    let f = ui.add(parent, e, Field::default());
    ids.insert(id, f);
}

fn notes_page(pages: Vec<JournalPage>) -> Notes {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let page = panel(ui, r, at(0.0, 0.0, 300.0, 500.0));
    let mut ids = HashMap::new();
    for id in [
        notes::LABEL_FIELD_ID,
        notes::TITLE_FIELD_ID,
        notes::NOTES_FIELD_ID,
        notes::LOCATION_TEXT_ID,
        notes::TIMER_DAYS_FIELD_ID,
        notes::TIMER_HOURS_FIELD_ID,
        notes::TIMER_MINUTES_FIELD_ID,
    ] {
        add_field(ui, &mut ids, page, id);
    }
    for id in [
        notes::PAGE_NUMBER_ID,
        notes::RUNNING_TIMER_TEXT_ID,
        notes::TIMER_DAYS_LABEL_ID,
        notes::TIMER_HOURS_LABEL_ID,
        notes::TIMER_MINUTES_LABEL_ID,
    ] {
        add_std_text(ui, &mut ids, page, id);
    }
    for id in [
        notes::PREVIOUS_BUTTON_ID,
        notes::NEXT_BUTTON_ID,
        notes::NEW_BUTTON_ID,
        notes::FIRST_BUTTON_ID,
        notes::LAST_BUTTON_ID,
        notes::RECORD_BUTTON_ID,
        notes::START_BUTTON_ID,
    ] {
        add_std_button(ui, &mut ids, page, id);
    }
    ui.register_window("journal", page, None, 0).unwrap();
    let c = JournalNotesPageController::bind(ui, page);
    host.attach_controller("journal", Box::new(c)).unwrap();
    let mut state = JournalState::new();
    state.load(pages);
    let mut n = Notes {
        host,
        ids,
        state,
        now: NOW,
    };
    n.tick();
    n
}

impl Notes {
    fn tick(&mut self) {
        let view = self.state.view();
        let now = self.now;
        self.host
            .with_controller::<JournalNotesPageController, _>("journal", |c, ui| {
                c.update(ui, &view, OUTDOOR_CELL, now);
            });
    }

    fn run(&mut self) {
        for out in panel_outputs(&mut self.host) {
            match out {
                PanelOutput::Session(SessionCommand::Journal(c)) => {
                    apply(&mut self.state, &c, self.now);
                }
                other => panic!("unexpected {other:?}"),
            }
        }
        self.tick();
    }

    fn click(&mut self, id: u32) {
        self.host.root.fire_event(kind::CLICK, self.ids[&id], None);
        self.host.pump();
        self.run();
    }

    fn field(&self, id: u32) -> String {
        self.host
            .root
            .widget::<Field>(self.ids[&id])
            .unwrap()
            .text()
    }

    fn type_into(&mut self, id: u32, text: &str) {
        self.host
            .root
            .widget_mut::<Field>(self.ids[&id])
            .unwrap()
            .set_text(text);
    }

    fn text(&self, id: u32) -> String {
        text_of(&self.host.root, self.ids[&id])
    }

    fn visible(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().visible()
    }
}

#[test]
fn record_shows_the_location_on_the_page() {
    let mut n = notes_page(vec![JournalPage::default()]);
    n.click(notes::RECORD_BUTTON_ID);
    assert!(n.state.current().has_location);
    assert_ne!(n.field(notes::LOCATION_TEXT_ID), "");
}

#[test]
fn record_on_an_empty_journal_does_nothing() {
    let mut n = notes_page(Vec::new());
    n.click(notes::RECORD_BUTTON_ID);
    assert_eq!(n.field(notes::LOCATION_TEXT_ID), "");
}

#[test]
fn starting_a_countdown_swaps_the_boxes_for_the_readout() {
    let mut n = notes_page(vec![JournalPage::default()]);
    n.type_into(notes::TIMER_HOURS_FIELD_ID, "1");
    n.click(notes::START_BUTTON_ID);
    for id in [
        notes::TIMER_DAYS_FIELD_ID,
        notes::TIMER_HOURS_FIELD_ID,
        notes::TIMER_MINUTES_FIELD_ID,
        notes::TIMER_DAYS_LABEL_ID,
        notes::TIMER_HOURS_LABEL_ID,
        notes::TIMER_MINUTES_LABEL_ID,
    ] {
        assert!(!n.visible(id), "{id:#010x}");
    }
    assert!(n.visible(notes::RUNNING_TIMER_TEXT_ID));
    assert_eq!(n.text(notes::RUNNING_TIMER_TEXT_ID), "1h 0s");
    let start = n.ids[&notes::START_BUTTON_ID];
    assert_eq!(
        n.host
            .root
            .widget::<Button>(start)
            .unwrap()
            .label
            .as_deref(),
        Some("Stop")
    );
}

#[test]
fn start_with_no_length_does_nothing() {
    let mut n = notes_page(vec![JournalPage::default()]);
    n.click(notes::START_BUTTON_ID);
    assert!(n.visible(notes::TIMER_DAYS_FIELD_ID));
    assert_eq!(n.text(notes::RUNNING_TIMER_TEXT_ID), "");
}

#[test]
fn the_countdown_counts_down() {
    let mut n = notes_page(vec![JournalPage::default()]);
    n.type_into(notes::TIMER_HOURS_FIELD_ID, "1");
    n.click(notes::START_BUTTON_ID);
    assert_eq!(n.text(notes::RUNNING_TIMER_TEXT_ID), "1h 0s");
    n.now = NOW + 30 * 60 * 1000;
    n.tick();
    assert_eq!(n.text(notes::RUNNING_TIMER_TEXT_ID), "30m 0s");
}

#[test]
fn start_again_stops_the_countdown_and_brings_the_boxes_back() {
    let mut n = notes_page(vec![JournalPage::default()]);
    n.type_into(notes::TIMER_HOURS_FIELD_ID, "1");
    n.click(notes::START_BUTTON_ID);
    n.click(notes::START_BUTTON_ID);
    assert!(n.visible(notes::TIMER_DAYS_FIELD_ID));
    assert!(n.visible(notes::TIMER_DAYS_LABEL_ID));
    assert_eq!(n.text(notes::RUNNING_TIMER_TEXT_ID), "");
}

#[test]
fn new_adds_a_page_and_shows_it() {
    let mut n = notes_page(Vec::new());
    n.click(notes::NEW_BUTTON_ID);
    assert_eq!(n.state.pages().len(), 1);
    assert_eq!(n.text(notes::PAGE_NUMBER_ID), "~ 1 ~");
}

#[test]
fn every_turn_commits_the_page_first() {
    let mut n = notes_page(vec![JournalPage::default(), JournalPage::default()]);
    n.type_into(notes::TITLE_FIELD_ID, "typed but not committed");
    n.click(notes::NEXT_BUTTON_ID);
    assert_eq!(n.state.pages()[0].title, "typed but not committed");
}

#[test]
fn leaving_a_field_commits_it() {
    let mut n = notes_page(vec![JournalPage::default()]);
    let title = n.ids[&notes::TITLE_FIELD_ID];
    n.host.root.set_keyboard_focus(Some(title));
    n.tick();
    n.type_into(notes::TITLE_FIELD_ID, "left behind");
    n.host.root.set_keyboard_focus(None);
    n.tick();
    n.run();
    assert_eq!(n.state.pages()[0].title, "left behind");
}

#[test]
fn first_and_last_jump_to_the_ends() {
    let mut n = notes_page(vec![JournalPage::default(); 3]);
    n.click(notes::LAST_BUTTON_ID);
    assert_eq!(n.text(notes::PAGE_NUMBER_ID), "~ 3 ~");
    n.click(notes::FIRST_BUTTON_ID);
    assert_eq!(n.text(notes::PAGE_NUMBER_ID), "~ 1 ~");
}

#[test]
fn previous_and_next_stop_at_the_ends() {
    let mut n = notes_page(vec![JournalPage::default(); 2]);
    n.click(notes::PREVIOUS_BUTTON_ID);
    assert_eq!(n.text(notes::PAGE_NUMBER_ID), "~ 1 ~");
    n.click(notes::NEXT_BUTTON_ID);
    n.click(notes::NEXT_BUTTON_ID);
    assert_eq!(n.text(notes::PAGE_NUMBER_ID), "~ 2 ~");
}

#[test]
fn an_empty_journal_shows_no_page_number() {
    let n = notes_page(Vec::new());
    assert_eq!(n.text(notes::PAGE_NUMBER_ID), "");
}

#[test]
fn turning_shows_that_pages_text() {
    let mut n = notes_page(vec![
        JournalPage {
            title: "first".into(),
            notes: "one".into(),
            ..JournalPage::default()
        },
        JournalPage {
            title: "second".into(),
            notes: "two".into(),
            ..JournalPage::default()
        },
    ]);
    n.click(notes::NEXT_BUTTON_ID);
    assert_eq!(n.field(notes::TITLE_FIELD_ID), "second");
    assert_eq!(n.field(notes::NOTES_FIELD_ID), "two");
}

#[test]
fn the_countdown_boxes_take_digits_only() {
    let mut n = notes_page(vec![JournalPage::default()]);
    let days = n.ids[&notes::TIMER_DAYS_FIELD_ID];
    n.host.root.set_keyboard_focus(Some(days));
    for c in "1a2".chars() {
        n.host.char_typed(c);
    }
    assert_eq!(n.field(notes::TIMER_DAYS_FIELD_ID), "12");
}

// ── The page list ───────────────────────────────────────────────────────

#[test]
fn the_search_matches_label_title_or_notes_with_case() {
    let page = JournalPage {
        label: "lab".into(),
        title: "tit".into(),
        notes: "not".into(),
        ..JournalPage::default()
    };
    for s in ["lab", "tit", "not"] {
        assert!(page_contains(&page, s));
    }
    assert!(!page_contains(&page, "zzz"));
    let aerlinthe = titled("Aerlinthe");
    assert!(page_contains(&aerlinthe, "Aer"));
    assert!(
        !page_contains(&aerlinthe, "aer"),
        "the game's search minds case"
    );
    assert!(page_contains(&JournalPage::default(), ""));
}

struct List {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    state: JournalState,
    opened: Vec<usize>,
}

fn row_template(ui: &mut UiRoot, layout: u32, element: u32) -> Option<ElementId> {
    if layout != pages::ROW_TEMPLATE_LAYOUT_ID || element != pages::ROW_TEMPLATE_ELEMENT_ID {
        return None;
    }
    let mut e = Element::at(0.0, 0.0, 270.0, 20.0);
    DatElement::configure(&mut e);
    let row = ui.insert(
        e,
        DatElement::new(std::sync::Arc::new(ElementInfo {
            kind: 3,
            width: 270.0,
            height: 20.0,
            ..ElementInfo::default()
        })),
    );
    for id in [pages::ROW_NUMBER_ID, pages::ROW_TITLE_ID] {
        let mut e = Element::at(0.0, 0.0, 90.0, 20.0);
        e.dat_element_id = id;
        Text::configure(&mut e);
        let mut t = Text::default();
        t.default_color = Color::rgba(0.8, 0.8, 0.8, 1.0);
        ui.add(row, e, t);
    }
    Some(row)
}

fn page_list(pages_in: Vec<JournalPage>) -> List {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let page = panel(ui, r, at(0.0, 0.0, 300.0, 500.0));
    let mut ids = HashMap::new();
    let mut e = Element::at(0.0, 0.0, 270.0, 430.0);
    e.dat_element_id = pages::LIST_ID;
    let mut list = ListBox::new(std::sync::Arc::new(ElementInfo {
        id: pages::LIST_ID,
        kind: 5,
        ..ElementInfo::default()
    }));
    list.templates = vec![TemplateEntry {
        layout: pages::ROW_TEMPLATE_LAYOUT_ID,
        element: pages::ROW_TEMPLATE_ELEMENT_ID,
    }];
    ids.insert(pages::LIST_ID, ui.add(page, e, list));
    add_field(ui, &mut ids, page, pages::SEARCH_FIELD_ID);
    add_std_button(ui, &mut ids, page, pages::DELETE_BUTTON_ID);
    add_std_button(ui, &mut ids, page, pages::RESET_BUTTON_ID);
    ui.register_window("list", page, None, 0).unwrap();
    let c = JournalPageListController::bind(ui, page);
    host.attach_controller("list", Box::new(PageListHost(c)))
        .unwrap();
    let mut state = JournalState::new();
    state.load(pages_in);
    let mut l = List {
        host,
        ids,
        state,
        opened: Vec::new(),
    };
    l.tick();
    l
}

/// The page list on its own, its opened rows reported as notices.
#[derive(Debug)]
struct PageListHost(JournalPageListController);

impl ac_ui::host::PanelController for PageListHost {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ac_ui::root::ElementMessage,
    ) -> bool {
        match self.0.on_message(ui, element, message) {
            Some(Some(page)) => {
                ui.emit(ac_ui::root::UiOutput::Panel(PanelOutput::Notice(format!(
                    "open {page}"
                ))));
                true
            }
            Some(None) => true,
            None => false,
        }
    }
}

impl List {
    fn with<R>(&mut self, f: impl FnOnce(&mut JournalPageListController, &mut UiRoot) -> R) -> R {
        self.host
            .with_controller::<PageListHost, _>("list", |c, ui| f(&mut c.0, ui))
            .unwrap()
    }

    fn tick(&mut self) {
        let view = self.state.view();
        let mut rows = row_template;
        self.with(|c, ui| c.update(ui, &view, &mut rows));
    }

    fn run(&mut self) {
        for out in panel_outputs(&mut self.host) {
            match out {
                PanelOutput::Session(SessionCommand::Journal(c)) => {
                    apply(&mut self.state, &c, NOW);
                }
                PanelOutput::Notice(n) => {
                    self.opened
                        .push(n.trim_start_matches("open ").parse().unwrap());
                }
                other => panic!("unexpected {other:?}"),
            }
        }
        self.tick();
    }

    fn click_button(&mut self, id: u32) {
        self.host.root.fire_event(kind::CLICK, self.ids[&id], None);
        self.host.pump();
        self.run();
    }

    fn row_pages(&mut self) -> Vec<usize> {
        self.with(|c, _| c.row_pages().to_vec())
    }

    fn selected(&mut self) -> usize {
        self.with(|c, _| c.selected_page())
    }

    fn click_row(&mut self, page: usize, at_ms: i64) {
        let opened = self.with(|c, ui| c.click(ui, page, at_ms));
        if let Some(p) = opened {
            self.opened.push(p);
        }
    }
}

#[test]
fn every_page_is_listed_with_its_number() {
    let mut l = page_list(vec![titled("one"), titled("two")]);
    assert_eq!(l.row_pages(), [1, 2]);
    let rows = l.with(|c, _| c.row_elements());
    let numbers: Vec<String> = rows
        .iter()
        .map(|r| {
            let n = l
                .host
                .root
                .find_descendant(*r, pages::ROW_NUMBER_ID)
                .unwrap();
            text_of(&l.host.root, n)
        })
        .collect();
    assert_eq!(numbers, ["1", "2"]);
}

#[test]
fn a_search_filters_the_list_and_keeps_the_real_numbers() {
    let mut l = page_list(vec![titled("alpha"), titled("beta"), titled("gamma")]);
    let search = l.ids[&pages::SEARCH_FIELD_ID];
    l.host
        .root
        .widget_mut::<Field>(search)
        .unwrap()
        .set_text("beta");
    l.tick();
    assert_eq!(l.row_pages(), [2]);
}

#[test]
fn a_filtered_out_selection_is_dropped() {
    let mut l = page_list(vec![titled("alpha"), titled("beta")]);
    l.with(|c, ui| c.select(ui, 1));
    let search = l.ids[&pages::SEARCH_FIELD_ID];
    l.host
        .root
        .widget_mut::<Field>(search)
        .unwrap()
        .set_text("beta");
    l.tick();
    assert_eq!(l.selected(), 0);
}

#[test]
fn delete_needs_a_selection_and_removes_it() {
    let mut l = page_list(vec![titled("only")]);
    l.click_button(pages::DELETE_BUTTON_ID);
    assert_eq!(l.state.pages().len(), 1);

    let mut l = page_list(vec![titled("one"), titled("two")]);
    l.with(|c, ui| c.select(ui, 1));
    l.click_button(pages::DELETE_BUTTON_ID);
    assert_eq!(l.state.pages().len(), 1);
    assert_eq!(l.state.pages()[0].title, "two");
    assert_eq!(l.selected(), 0);
}

#[test]
fn one_click_selects_two_open_a_third_does_not_reopen() {
    let mut l = page_list(vec![titled("one")]);
    l.click_row(1, NOW);
    assert_eq!(l.selected(), 1);
    assert!(l.opened.is_empty());
    l.click_row(1, NOW + 10);
    assert_eq!(l.opened, [1]);
    l.click_row(1, NOW + 20);
    assert_eq!(l.opened, [1]);
}

#[test]
fn clicks_on_different_rows_are_not_a_double_click() {
    let mut l = page_list(vec![titled("one"), titled("two")]);
    l.click_row(1, NOW);
    l.click_row(2, NOW + 10);
    assert!(l.opened.is_empty());
    assert_eq!(l.selected(), 2);
}

#[test]
fn the_double_click_window_is_a_full_second() {
    let mut l = page_list(vec![titled("one")]);
    l.click_row(1, NOW);
    l.click_row(1, NOW + 900);
    assert_eq!(l.opened, [1]);
    l.opened.clear();
    l.click_row(1, NOW + 10_000);
    l.click_row(1, NOW + 11_100);
    assert!(l.opened.is_empty());
}

#[test]
fn the_selected_title_is_white() {
    let mut l = page_list(vec![titled("one"), titled("two")]);
    l.click_row(2, NOW);
    let rows = l.with(|c, _| c.row_elements());
    let color = |l: &List, r: ElementId| {
        let t = l.host.root.find_descendant(r, pages::ROW_TITLE_ID).unwrap();
        l.host.root.widget::<Text>(t).unwrap().content_lines()[0].color
    };
    assert_eq!(color(&l, rows[1]), Color::WHITE);
    assert_eq!(color(&l, rows[0]), Color::rgba(0.8, 0.8, 0.8, 1.0));
}

#[test]
fn a_row_click_through_the_ui_selects_it() {
    let mut l = page_list(vec![titled("one"), titled("two")]);
    let rows = l.with(|c, _| c.row_elements());
    l.host.root.fire_event(kind::CLICK, rows[1], None);
    l.host.pump();
    l.run();
    assert_eq!(l.selected(), 2);
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import_rooted};
    use ac_ui::panels::DatRowTemplates;
    use ac_ui::panels::journal::{self, JournalPanelController};
    use std::path::PathBuf;
    use std::sync::Arc;

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

    /// OpenAC `JournalPanelLiveBindTests` and
    /// `JournalPanelButtonsAreClickableTests`: the notes page's fields and
    /// buttons bind as typeable fields and clickable buttons, the page
    /// list's rows come from their template, and a double click opens the
    /// page on the notes tab.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_notes_page_and_page_list_bind_against_the_dats() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            journal::HOST_LAYOUT_ID,
            journal::SLOT_ELEMENT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("journal layout");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window("journal", layout.root, None, 0)
            .unwrap();
        let ui = &mut host.root;
        for id in [
            notes::LABEL_FIELD_ID,
            notes::TITLE_FIELD_ID,
            notes::NOTES_FIELD_ID,
            notes::TIMER_DAYS_FIELD_ID,
            notes::TIMER_HOURS_FIELD_ID,
            notes::TIMER_MINUTES_FIELD_ID,
            notes::LOCATION_TEXT_ID,
            pages::SEARCH_FIELD_ID,
        ] {
            let e = layout.find(id).unwrap_or_else(|| panic!("{id:#010x}"));
            assert!(ui.widget::<Field>(e).is_some(), "{id:#010x} is a field");
        }
        for id in [
            notes::PREVIOUS_BUTTON_ID,
            notes::NEXT_BUTTON_ID,
            notes::NEW_BUTTON_ID,
            notes::FIRST_BUTTON_ID,
            notes::LAST_BUTTON_ID,
            notes::RECORD_BUTTON_ID,
            notes::START_BUTTON_ID,
            pages::DELETE_BUTTON_ID,
            pages::RESET_BUTTON_ID,
        ] {
            let e = layout.find(id).unwrap_or_else(|| panic!("{id:#010x}"));
            assert!(ui.widget::<Button>(e).is_some(), "{id:#010x} is a button");
        }
        let list = layout.find(pages::LIST_ID).unwrap();
        assert_eq!(
            ui.widget::<ListBox>(list)
                .unwrap()
                .templates
                .first()
                .copied(),
            Some(TemplateEntry {
                layout: pages::ROW_TEMPLATE_LAYOUT_ID,
                element: pages::ROW_TEMPLATE_ELEMENT_ID,
            })
        );
        let mut ctl = JournalPanelController::bind(ui, &layout).expect("a tab panel");
        ctl.activate_tabs(ui);
        host.attach_controller("journal", Box::new(ctl)).unwrap();
        let mut state = JournalState::new();
        state.load(vec![titled("Aerlinthe"), titled("Holtburg")]);
        let view = state.view();
        let mut rows = DatRowTemplates::new(&c, c.font(DEFAULT_FONT_ID));
        host.with_controller::<JournalPanelController, _>("journal", |w, ui| {
            w.show_page_list(ui);
            w.update_journal(ui, &view, 0, NOW, &mut rows);
        });
        let row_elements = host
            .with_controller::<JournalPanelController, _>("journal", |w, _| {
                w.page_list().unwrap().row_elements()
            })
            .unwrap();
        assert_eq!(row_elements.len(), 2);
        let title = host
            .root
            .find_descendant(row_elements[1], pages::ROW_TITLE_ID)
            .unwrap();
        assert_eq!(text_of(&host.root, title), "Holtburg");
        // Two clicks on the second row open it on the notes tab.
        host.take_outputs();
        for _ in 0..2 {
            host.root.fire_event(kind::CLICK, row_elements[1], None);
            host.pump();
        }
        let out = host.take_outputs();
        assert!(
            out.iter().any(|o| matches!(
                o,
                ac_ui::root::UiOutput::Panel(PanelOutput::Session(SessionCommand::Journal(
                    JournalCommand::GotoPage(2)
                )))
            )),
            "{out:?}"
        );
        assert!(
            host.with_controller::<JournalPanelController, _>("journal", |w, ui| w
                .is_showing_notes(ui))
                .unwrap()
        );
        host.tick(0.016, 16, None);
        assert!(!host.draw((1024.0, 768.0), &c).main.is_empty());
        assert!(c.take_errors().is_empty());
    }
}
