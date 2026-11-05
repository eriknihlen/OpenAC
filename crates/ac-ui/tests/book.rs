//! The book reader.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/BookPanelControllerTests.cs`
//! on a slot built from the authored element ids (the real one is imported
//! in `real_dats`, BookPanelLiveDatTests). OpenAC's controller calls its
//! book state directly; here it asks with `BookCommand`s, which the
//! harness carries out on the runtime's own `BookState` (as the session
//! does) and feeds back, recording what would be sent.

mod common;

use std::collections::HashMap;

use ac_proto::book::{BookPage, BookPageList, OpenBook, PageDataResponse, PageResponse};
use ac_runtime::gameplay::{
    BookCommand, BookFlushAction, BookPageAction, BookState, GameplayCommand,
};
use ac_runtime::session::SessionCommand;
use ac_ui::ElementId;
use ac_ui::element_info::ElementInfo;
use ac_ui::event::kind;
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::book::{self, BookContext, BookPanelController, page_label};
use ac_ui::panels::{PanelOutput, panel_id};
use ac_ui::root::ElementMessage;
use ac_ui::tree::Element;
use ac_ui::widgets::{Field, Menu, ScrollSource, Scrollbar};
use common::items::panel_outputs;
use common::panels::*;
use common::*;

const PLAYER: u32 = 0x5000_000A;
const OTHER: u32 = 0x5000_000B;
const BOOK: u32 = 0x8000_1234;
const WINDOW: &str = "book";

#[derive(Debug, Default)]
struct Sent {
    page_text_requests: Vec<(u32, i32)>,
    add_page_requests: Vec<u32>,
    saved: Vec<(u32, i32, String)>,
    deleted: Vec<(u32, i32)>,
    visibility: Vec<bool>,
}

struct Reader {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
    state: BookState,
    sent: Sent,
    shows_account: bool,
}

fn page(author: u32, text: &str) -> BookPage {
    BookPage {
        author_id: author,
        author_name: "Someone".into(),
        author_account: "acct".into(),
        text_included: 1,
        ignore_author: 0,
        page_text: text.into(),
    }
}

fn open_book(max_pages: i32, scribe_id: u32, pages: Vec<BookPage>) -> OpenBook {
    OpenBook {
        book_guid: BOOK,
        max_num_pages: max_pages,
        pages: BookPageList {
            max_num_pages: max_pages,
            max_num_chars_per_page: 500,
            pages,
        },
        inscription: "The Grand Inscription".into(),
        scribe_id,
        scribe_name: "Scribbler".into(),
    }
}

/// The slot: title, page text (a field), the page buttons, the menu with
/// its face text, the close button and the page's bar.
fn reader_with(field: bool) -> Reader {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 400.0, 300.0));
    let mut ids = HashMap::new();
    add_std_text(ui, &mut ids, root, book::TITLE_TEXT_ID);
    if field {
        let mut e = Element::at(0.0, 20.0, 200.0, 120.0);
        e.dat_element_id = book::PAGE_TEXT_ID;
        Field::configure(&mut e);
        let f = ui.add(root, e, Field::default());
        ids.insert(book::PAGE_TEXT_ID, f);
    } else {
        add_std_text(ui, &mut ids, root, book::PAGE_TEXT_ID);
    }
    for id in [
        book::PREVIOUS_BUTTON_ID,
        book::NEXT_BUTTON_ID,
        book::CLOSE_BUTTON_ID,
    ] {
        add_std_button(ui, &mut ids, root, id);
    }
    let mut e = Element::at(0.0, 150.0, 292.0, 18.0);
    e.dat_element_id = book::PAGE_MENU_ID;
    Menu::configure(&mut e);
    let menu = ui.add(root, e, Menu::default());
    ids.insert(book::PAGE_MENU_ID, menu);
    add_std_text(ui, &mut ids, menu, book::PAGE_NUMBER_TEXT_ID);
    let mut e = Element::at(210.0, 20.0, 16.0, 240.0);
    e.dat_element_id = book::PAGE_SCROLLBAR_ID;
    let bar = ui.add(root, e, Scrollbar::default());
    ids.insert(book::PAGE_SCROLLBAR_ID, bar);
    let layout = ImportedLayout::from_parts(root, ids.clone());
    ui.register_window(WINDOW, root, None, 0).unwrap();
    let c = BookPanelController::bind(ui, &layout).expect("binds");
    host.attach_controller(WINDOW, Box::new(c)).unwrap();
    Reader {
        host,
        ids,
        state: BookState::default(),
        sent: Sent::default(),
        shows_account: false,
    }
}

fn reader() -> Reader {
    reader_with(true)
}

impl Reader {
    /// The window takes the book as it stands.
    fn tick(&mut self) {
        let view = self.state.view();
        let context = BookContext {
            player: PLAYER,
            book_name: "Parchment",
            shows_account: self.shows_account,
        };
        let shown = self
            .host
            .with_controller::<BookPanelController, _>(WINDOW, |c, ui| {
                c.update(ui, &view, &context)
            })
            .unwrap();
        if let Some(open) = shown {
            self.sent.visibility.push(open);
        }
    }

    fn open(&mut self, book: &OpenBook) {
        self.state.apply_open_book(book, PLAYER);
        self.tick();
    }

    fn record_flush(&mut self, flush: BookFlushAction, page: i32, text: Option<&str>) {
        match flush {
            BookFlushAction::ModifyPage => {
                self.sent
                    .saved
                    .push((BOOK, page, text.unwrap_or_default().to_owned()));
            }
            BookFlushAction::DeletePage => self.sent.deleted.push((BOOK, page)),
            BookFlushAction::None => {}
        }
    }

    /// Carry out what the reader asked, as the session does, then show the
    /// book again.
    fn run(&mut self) {
        for out in panel_outputs(&mut self.host) {
            match out {
                PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Book(c))) => match c
                {
                    BookCommand::TurnWriting { page, typed } => {
                        let turn = self.state.turn_page(page, typed.as_deref(), PLAYER);
                        self.record_flush(turn.flush, turn.flush_page, typed.as_deref());
                        match turn.action {
                            BookPageAction::RequestPageText => {
                                self.sent.page_text_requests.push((BOOK, turn.page));
                            }
                            BookPageAction::AddPage => self.sent.add_page_requests.push(BOOK),
                            _ => {}
                        }
                    }
                    BookCommand::CloseWriting(typed) => {
                        if let Some(t) = typed.as_deref() {
                            let (flush, page) = self.state.flush_current_page(t, PLAYER);
                            self.record_flush(flush, page, Some(t));
                        }
                        self.state.close_book();
                    }
                    other => panic!("unexpected {other:?}"),
                },
                PanelOutput::ClosePanel(panel_id::BOOK) => self.sent.visibility.push(false),
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

    fn field(&self) -> &Field {
        self.host
            .root
            .widget::<Field>(self.ids[&book::PAGE_TEXT_ID])
            .unwrap()
    }

    fn type_page(&mut self, text: &str) {
        self.host
            .root
            .widget_mut::<Field>(self.ids[&book::PAGE_TEXT_ID])
            .unwrap()
            .set_text(text);
    }

    fn text(&self, id: u32) -> String {
        text_of(&self.host.root, self.ids[&id])
    }

    fn menu(&self) -> &Menu {
        self.host
            .root
            .widget::<Menu>(self.ids[&book::PAGE_MENU_ID])
            .unwrap()
    }

    fn enabled(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().enabled
    }

    fn hide(&mut self) {
        self.host
            .with_controller::<BookPanelController, _>(WINDOW, |c, ui| c.on_hidden(ui));
        self.run();
    }
}

#[test]
fn bind_needs_the_page_text() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let slot = panel(&mut ui, r, at(0.0, 0.0, 400.0, 300.0));
    let layout = ImportedLayout::from_parts(slot, HashMap::new());
    assert!(BookPanelController::bind(&mut ui, &layout).is_none());
}

#[test]
fn opening_a_book_shows_the_panel_with_the_first_page() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(OTHER, "In the beginning")]));
    assert_eq!(r.sent.visibility, [true]);
    assert_eq!(r.field().text(), "In the beginning");
    assert_eq!(r.text(book::PAGE_NUMBER_TEXT_ID), "Page 1  - by Someone ");
}

#[test]
fn an_inscribed_book_is_titled_by_its_inscription_and_another_by_the_item() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(OTHER, "text")]));
    assert_eq!(r.text(book::TITLE_TEXT_ID), "The Grand Inscription");
    let mut r = reader();
    r.open(&open_book(4, 0, vec![page(OTHER, "text")]));
    assert_eq!(r.text(book::TITLE_TEXT_ID), "Parchment");
}

#[test]
fn next_turns_to_a_page_in_hand() {
    let mut r = reader();
    r.open(&open_book(
        4,
        OTHER,
        vec![page(OTHER, "one"), page(OTHER, "two")],
    ));
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.state.current_page(), 1);
    assert_eq!(r.field().text(), "two");
    assert_eq!(r.text(book::PAGE_NUMBER_TEXT_ID), "Page 2  - by Someone ");
    assert!(r.sent.page_text_requests.is_empty());
    assert!(r.sent.add_page_requests.is_empty());
}

#[test]
fn next_asks_for_a_page_that_came_without_its_text() {
    let mut r = reader();
    let mut bare = page(OTHER, "");
    bare.text_included = 0;
    r.open(&open_book(4, OTHER, vec![page(OTHER, "one"), bare]));
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.sent.page_text_requests, [(BOOK, 1)]);
    assert_eq!(r.field().text(), "");
    r.state.apply_page_data(&PageDataResponse {
        book_guid: BOOK,
        page_number: 1,
        page: page(OTHER, "fetched"),
    });
    r.tick();
    assert_eq!(r.field().text(), "fetched");
}

#[test]
fn next_asks_for_a_new_page_off_the_end_of_a_writable_book() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(PLAYER, "one")]));
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.sent.add_page_requests, [BOOK]);
}

#[test]
fn next_stops_at_the_end_of_a_book_that_cannot_grow() {
    let mut r = reader();
    r.open(&open_book(1, OTHER, vec![page(OTHER, "only page")]));
    assert!(!r.enabled(book::NEXT_BUTTON_ID));
    r.click(book::NEXT_BUTTON_ID);
    assert!(r.sent.add_page_requests.is_empty());
    assert_eq!(r.state.current_page(), 0);
}

#[test]
fn previous_turns_back_and_stops_at_the_first_page() {
    let mut r = reader();
    r.open(&open_book(
        4,
        OTHER,
        vec![page(OTHER, "one"), page(OTHER, "two")],
    ));
    r.click(book::NEXT_BUTTON_ID);
    r.click(book::PREVIOUS_BUTTON_ID);
    assert_eq!(r.state.current_page(), 0);
    r.click(book::PREVIOUS_BUTTON_ID);
    assert_eq!(r.state.current_page(), 0);
}

#[test]
fn the_page_menu_lists_every_slot_and_follows_the_open_page() {
    let mut r = reader();
    r.open(&open_book(
        4,
        OTHER,
        vec![page(OTHER, "one"), page(OTHER, "two")],
    ));
    let labels: Vec<String> = r.menu().items.iter().map(|i| i.label.clone()).collect();
    assert_eq!(
        labels,
        [
            "Page 1  - by Someone ",
            "Page 2  - by Someone ",
            "Page 3  - ",
            "Page 4  - ",
        ]
    );
    assert_eq!(r.menu().selected, Some(0));
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.menu().selected, Some(1));
}

#[test]
fn a_pick_from_the_page_menu_turns_there() {
    let mut r = reader();
    r.open(&open_book(
        4,
        OTHER,
        vec![page(OTHER, "one"), page(OTHER, "two")],
    ));
    let menu = r.ids[&book::PAGE_MENU_ID];
    r.host
        .root
        .send_message(menu, ElementMessage::MenuSelected(1));
    r.host.pump();
    r.run();
    assert_eq!(r.state.current_page(), 1);
    assert_eq!(r.field().text(), "two");
}

#[test]
fn hiding_the_panel_closes_the_book() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(OTHER, "one")]));
    r.hide();
    assert!(!r.state.is_open());
    assert_eq!(r.field().text(), "");
    assert_eq!(r.text(book::TITLE_TEXT_ID), "");
    assert!(r.menu().items.is_empty());
}

#[test]
fn a_book_closed_elsewhere_takes_the_panel_down() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(OTHER, "one")]));
    r.state.reset_session();
    r.tick();
    assert_eq!(r.sent.visibility, [true, false]);
}

#[test]
fn nothing_happens_while_the_book_has_not_changed_or_reopens() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(OTHER, "one")]));
    r.tick();
    r.tick();
    r.open(&open_book(
        4,
        OTHER,
        vec![page(OTHER, "one"), page(OTHER, "two")],
    ));
    assert_eq!(r.sent.visibility, [true]);
}

#[test]
fn who_may_type_on_a_page() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(PLAYER, "mine")]));
    assert!(r.field().editable());
    assert_eq!(r.field().max_characters, 500);

    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(OTHER, "theirs")]));
    assert!(!r.field().editable());

    let mut shared = page(OTHER, "shared");
    shared.ignore_author = 1;
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![shared]));
    assert!(r.field().editable(), "a communal page");

    let mut bare = page(PLAYER, "");
    bare.text_included = 0;
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![bare]));
    assert!(!r.field().editable(), "its text has not arrived");
}

#[test]
fn turning_away_from_an_edited_page_saves_it() {
    let mut r = reader();
    r.open(&open_book(
        4,
        OTHER,
        vec![page(PLAYER, "old"), page(PLAYER, "two")],
    ));
    r.type_page("rewritten");
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.sent.saved, [(BOOK, 0, "rewritten".to_owned())]);
    assert_eq!(r.state.page(0).unwrap().page_text, "rewritten");
    assert_eq!(r.field().text(), "two");
}

#[test]
fn turning_away_from_someone_elses_page_saves_nothing() {
    let mut r = reader();
    r.open(&open_book(
        4,
        OTHER,
        vec![page(OTHER, "theirs"), page(OTHER, "two")],
    ));
    r.click(book::NEXT_BUTTON_ID);
    assert!(r.sent.saved.is_empty());
    assert!(r.sent.deleted.is_empty());
}

#[test]
fn blanking_your_own_page_and_turning_on_deletes_it_and_follows_the_shift() {
    let mut r = reader();
    r.open(&open_book(
        4,
        OTHER,
        vec![
            page(PLAYER, "one"),
            page(PLAYER, "two"),
            page(PLAYER, "three"),
        ],
    ));
    r.click(book::NEXT_BUTTON_ID);
    r.type_page("   ");
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.sent.deleted, [(BOOK, 1)]);
    assert_eq!(r.sent.saved, [(BOOK, 0, "one".to_owned())]);
    assert_eq!(r.field().text(), "three");
    assert_eq!(r.state.pages().len(), 2);
}

#[test]
fn closing_saves_the_open_page_first_unless_it_is_read_only() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(PLAYER, "old")]));
    r.type_page("final words");
    r.hide();
    assert_eq!(r.sent.saved, [(BOOK, 0, "final words".to_owned())]);
    assert!(!r.state.is_open());

    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(OTHER, "lore")]));
    r.hide();
    assert!(r.sent.saved.is_empty() && r.sent.deleted.is_empty());
}

#[test]
fn an_added_page_comes_back_blank_and_typeable() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(PLAYER, "one")]));
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.sent.add_page_requests, [BOOK]);
    r.state.apply_add_page_response(
        &PageResponse {
            book_guid: BOOK,
            page_number: 1,
            success: true,
        },
        PLAYER,
        "Acdream",
    );
    r.tick();
    assert_eq!(r.field().text(), "");
    assert!(r.field().editable());
    assert_eq!(r.state.pages().len(), 2);
}

#[test]
fn no_turn_happens_while_a_request_is_in_flight() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(PLAYER, "one")]));
    r.click(book::NEXT_BUTTON_ID);
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.sent.add_page_requests.len(), 1);
}

#[test]
fn the_page_text_is_made_typeable_before_the_build() {
    let mut root = ElementInfo {
        id: book::SLOT_ELEMENT_ID,
        ..ElementInfo::default()
    };
    root.children.push(ElementInfo {
        id: book::PAGE_TEXT_ID,
        ..ElementInfo::default()
    });
    assert_eq!(root.children[0].effective_bool(0x16, None), None);
    book::mark_page_text_typeable(&mut root);
    assert_eq!(root.children[0].effective_bool(0x16, None), Some(true));
    book::mark_page_text_typeable(&mut root);
    assert_eq!(root.children[0].effective_bool(0x16, None), Some(true));
    let mut empty = ElementInfo {
        id: book::SLOT_ELEMENT_ID,
        ..ElementInfo::default()
    };
    book::mark_page_text_typeable(&mut empty);
    assert!(empty.children.is_empty());
}

#[test]
fn the_page_text_kind_is_what_the_slot_built() {
    let mut r = reader();
    let kind = r
        .host
        .with_controller::<BookPanelController, _>(WINDOW, |c, _| c.page_text_kind());
    assert_eq!(kind, Some("typeable field"));
    let mut r = reader_with(false);
    let kind = r
        .host
        .with_controller::<BookPanelController, _>(WINDOW, |c, _| c.page_text_kind());
    assert_eq!(kind, Some("read-only text"));
}

#[test]
fn the_page_buttons_are_off_at_the_ends_and_with_no_book() {
    let mut r = reader();
    assert!(!r.enabled(book::PREVIOUS_BUTTON_ID));
    assert!(!r.enabled(book::NEXT_BUTTON_ID));
    r.open(&open_book(
        2,
        OTHER,
        vec![page(OTHER, "one"), page(OTHER, "two")],
    ));
    assert!(!r.enabled(book::PREVIOUS_BUTTON_ID));
    assert!(r.enabled(book::NEXT_BUTTON_ID));
    r.click(book::NEXT_BUTTON_ID);
    assert!(r.enabled(book::PREVIOUS_BUTTON_ID));
    assert!(!r.enabled(book::NEXT_BUTTON_ID));
}

#[test]
fn a_page_label_reads_the_number_then_who_wrote_it() {
    type Case<'a> = (i32, Option<&'a str>, Option<&'a str>, bool, &'a str);
    let cases: [Case<'_>; 7] = [
        (
            7,
            Some("F.P."),
            Some("prewritten"),
            true,
            "Page 8  - by F.P. <prewritten>",
        ),
        (
            0,
            Some(""),
            Some("prewritten"),
            true,
            "Page 1  - <prewritten>",
        ),
        (
            0,
            Some("   "),
            Some("prewritten"),
            true,
            "Page 1  - <prewritten>",
        ),
        (2, Some("Alinta"), Some(""), true, "Page 3  - by Alinta "),
        (2, Some("Alinta"), None, true, "Page 3  - by Alinta "),
        (
            7,
            Some("F.P."),
            Some("prewritten"),
            false,
            "Page 8  - by F.P. ",
        ),
        (0, Some(""), Some("prewritten"), false, "Page 1  - "),
    ];
    for (index, author, account, shows, expected) in cases {
        assert_eq!(page_label(index, author, account, shows), expected);
    }
}

#[test]
fn a_prewritten_book_names_its_account_to_those_who_may_see_it() {
    let mut r = reader();
    r.shows_account = true;
    let mut p = page(OTHER, "one");
    p.author_name = "F.P.".into();
    p.author_account = "prewritten".into();
    r.open(&open_book(8, OTHER, vec![p]));
    assert_eq!(r.menu().items[0].label, "Page 1  - by F.P. <prewritten>");
    assert_eq!(
        r.text(book::PAGE_NUMBER_TEXT_ID),
        "Page 1  - by F.P. <prewritten>"
    );
}

#[test]
fn the_close_button_saves_closes_and_takes_the_panel_down() {
    let mut r = reader();
    r.open(&open_book(4, OTHER, vec![page(PLAYER, "one")]));
    r.type_page("last words");
    r.click(book::CLOSE_BUTTON_ID);
    assert_eq!(r.sent.saved, [(BOOK, 0, "last words".to_owned())]);
    assert!(!r.state.is_open());
    assert_eq!(r.sent.visibility, [true, false]);
    // Hidden again by the panel frame: nothing more is sent.
    r.hide();
    assert_eq!(r.sent.saved.len(), 1);
}

#[test]
fn the_page_text_drives_the_bar_beside_it() {
    let r = reader();
    let bar = r
        .host
        .root
        .widget::<Scrollbar>(r.ids[&book::PAGE_SCROLLBAR_ID])
        .unwrap();
    assert_eq!(bar.model, ScrollSource::Element(r.ids[&book::PAGE_TEXT_ID]));
}

#[test]
fn the_open_list_is_one_column_the_width_of_the_closed_row() {
    let mut r = reader();
    r.open(&open_book(40, OTHER, vec![page(OTHER, "one")]));
    let m = r.menu();
    assert!(m.scrollable && !m.open_upward);
    assert_eq!(m.items.len(), 40);
    assert!(
        (m.popup_outer_width() - 292.0).abs() < 1.0,
        "{}",
        m.popup_outer_width()
    );
    assert!(m.popup_outer_height() < 200.0);
    assert_eq!(m.item_normal_sprite, m.normal_sprite);
}

#[test]
fn the_face_shows_the_entry_the_reader_is_on_and_nothing_with_no_book() {
    let mut r = reader();
    assert_eq!(r.menu().button_label, "");
    assert_eq!(r.text(book::PAGE_NUMBER_TEXT_ID), "");
    r.open(&open_book(
        4,
        OTHER,
        vec![page(OTHER, "one"), page(OTHER, "two")],
    ));
    assert_eq!(r.menu().button_label, "Page 1  - by Someone ");
    r.click(book::NEXT_BUTTON_ID);
    assert_eq!(r.menu().button_label, "Page 2  - by Someone ");
}

#[test]
fn next_off_a_blank_last_page_you_wrote_does_nothing() {
    let mut r = reader();
    r.open(&open_book(
        4,
        OTHER,
        vec![page(PLAYER, "one"), page(PLAYER, "two")],
    ));
    r.click(book::NEXT_BUTTON_ID);
    r.sent.saved.clear();
    r.type_page("  ");
    r.click(book::NEXT_BUTTON_ID);
    assert!(r.sent.saved.is_empty());
    assert!(r.sent.deleted.is_empty());
    assert!(r.sent.add_page_requests.is_empty());
    assert_eq!(r.state.current_page(), 1);
    assert_eq!(r.state.pages().len(), 2);
}

// ── Real dats (BookPanelLiveDatTests) ───────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{BuildContext, LayoutSource, build, import_infos_rooted};
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

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_book_slot_imports_with_a_typeable_page_and_reads_a_book() {
        let c = content();
        let mut info =
            import_infos_rooted(&c, book::HOST_LAYOUT_ID, book::SLOT_ELEMENT_ID).expect("slot");
        book::mark_page_text_typeable(&mut info);
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let ctx = BuildContext {
            source: &c,
            default_font: c.font(DEFAULT_FONT_ID),
            source_layout_did: book::HOST_LAYOUT_ID,
            media_seconds: 0.0,
        };
        let layout = build(&mut host.root, &info, &ctx);
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window(WINDOW, layout.root, None, 0)
            .unwrap();
        for id in [
            book::TITLE_TEXT_ID,
            book::PAGE_TEXT_ID,
            book::PREVIOUS_BUTTON_ID,
            book::NEXT_BUTTON_ID,
            book::PAGE_MENU_ID,
            book::CLOSE_BUTTON_ID,
        ] {
            assert!(
                host.root.find_descendant(layout.root, id).is_some(),
                "{id:#010x}"
            );
        }
        let ctl = BookPanelController::bind(&mut host.root, &layout).expect("binds");
        assert_eq!(ctl.page_text_kind(), "typeable field");
        host.attach_controller(WINDOW, Box::new(ctl)).unwrap();
        let mut state = BookState::default();
        state.apply_open_book(&open_book(4, 0, vec![page(PLAYER, "Dear diary")]), PLAYER);
        let view = state.view();
        let context = BookContext {
            player: PLAYER,
            book_name: "Journal",
            shows_account: false,
        };
        let shown = host
            .with_controller::<BookPanelController, _>(WINDOW, |c, ui| {
                c.update(ui, &view, &context)
            })
            .unwrap();
        assert_eq!(shown, Some(true));
        let page = host
            .root
            .find_descendant(layout.root, book::PAGE_TEXT_ID)
            .unwrap();
        let field = host.root.widget::<Field>(page).unwrap();
        assert_eq!(field.text(), "Dear diary");
        assert!(field.editable());
        host.tick(0.016, 16, None);
        assert!(!host.draw((1024.0, 768.0), &c).main.is_empty());
        assert!(c.take_errors().is_empty());
    }
}
