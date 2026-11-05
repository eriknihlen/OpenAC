//! Ported from OpenAC `tests/AcDream.Runtime.Tests/RuntimeBookStateTests.cs`.

use ac_proto::book::BookPageList;

use super::*;

const PLAYER: u32 = 0x5000_000A;
const OTHER: u32 = 0x5000_000B;
const BOOK: u32 = 0x8000_1234;

fn page(author: u32, text: &str) -> BookPage {
    page_with(author, text, 1, 0)
}

fn page_with(author: u32, text: &str, text_included: u32, ignore_author: u32) -> BookPage {
    BookPage {
        author_id: author,
        author_name: "Someone".into(),
        author_account: "acct".into(),
        text_included,
        ignore_author,
        page_text: text.into(),
    }
}

fn open(pages: Vec<BookPage>) -> OpenBook {
    open_book(BOOK, 8, 500, pages)
}

fn open_book(guid: u32, max_pages: i32, max_chars: i32, pages: Vec<BookPage>) -> OpenBook {
    OpenBook {
        book_guid: guid,
        max_num_pages: max_pages,
        pages: BookPageList {
            max_num_pages: max_pages,
            max_num_chars_per_page: max_chars,
            pages,
        },
        inscription: "An Inscription".into(),
        scribe_id: OTHER,
        scribe_name: "Scribbler".into(),
    }
}

fn mine(texts: &[&str]) -> Vec<BookPage> {
    texts.iter().map(|t| page(PLAYER, t)).collect()
}

fn state_with(pages: Vec<BookPage>) -> BookState {
    let mut state = BookState::default();
    state.apply_open_book(&open(pages), PLAYER);
    state
}

fn text(state: &BookState, index: i32) -> &str {
    &state.page(index).unwrap().page_text
}

fn data(guid: u32, number: i32, page: BookPage) -> PageDataResponse {
    PageDataResponse {
        book_guid: guid,
        page_number: number,
        page,
    }
}

fn added(number: i32, success: bool) -> PageResponse {
    PageResponse {
        book_guid: BOOK,
        page_number: number,
        success,
    }
}

fn inscription(guid: u32, text: &str) -> Inscription {
    Inscription {
        object_guid: guid,
        text: text.into(),
        scribe_id: PLAYER,
        scribe_name: "Acdream".into(),
        scribe_account: "testaccount".into(),
    }
}

#[test]
fn opening_stores_the_book_and_opens_the_first_page() {
    let state = state_with(mine(&["one", "two"]));
    let view = state.view();
    assert!(state.is_open());
    assert_eq!(
        (
            view.book_guid,
            view.max_num_pages,
            view.max_num_chars_per_page,
            view.pages.len(),
            view.current_page
        ),
        (BOOK, 8, 500, 2, 0)
    );
    assert_eq!(
        (
            view.inscription.as_str(),
            view.scribe_id,
            view.scribe_name.as_str()
        ),
        ("An Inscription", OTHER, "Scribbler")
    );
    assert!(!view.request_pending);
}

#[test]
fn reopening_the_same_book_keeps_the_page() {
    let mut state = state_with(mine(&["one", "two", "three"]));
    assert_eq!(state.set_current_page(2, PLAYER), BookPageAction::Display);
    state.apply_open_book(&open(mine(&["one", "two", "three"])), PLAYER);
    assert_eq!(state.current_page(), 2);
}

#[test]
fn reopening_falls_back_to_the_last_page_that_exists() {
    let mut state = state_with(mine(&["one", "two", "three"]));
    state.set_current_page(2, PLAYER);
    state.apply_open_book(&open(mine(&["one"])), PLAYER);
    assert_eq!(state.current_page(), 0);
}

#[test]
fn an_empty_parchment_asks_for_its_first_page() {
    let mut state = BookState::default();
    let turn = state.apply_open_book(&open(Vec::new()), PLAYER);
    assert_eq!((turn.action, turn.page), (BookPageAction::AddPage, 0));
    assert!(state.request_pending());
    assert_eq!((state.current_page(), state.pages().len()), (0, 0));
}

#[test]
fn a_first_page_without_text_asks_for_it() {
    let mut state = BookState::default();
    let turn = state.apply_open_book(&open(vec![page_with(OTHER, "", 0, 0)]), PLAYER);
    assert_eq!(
        (turn.action, turn.page),
        (BookPageAction::RequestPageText, 0)
    );
    assert!(state.request_pending());
}

#[test]
fn a_readable_page_asks_for_nothing() {
    let mut state = BookState::default();
    let turn = state.apply_open_book(&open(vec![page(OTHER, "readable")]), PLAYER);
    assert_eq!(turn.action, BookPageAction::Display);
    assert!(!state.request_pending());
}

#[test]
fn a_different_book_does_not_carry_the_page() {
    let mut state = state_with(mine(&["one", "two"]));
    state.set_current_page(1, PLAYER);
    state.apply_open_book(&open_book(0x8000_9999, 8, 500, mine(&["a", "b"])), PLAYER);
    assert_eq!(state.current_page(), 0);
}

#[test]
fn turning_to_a_page_with_text_displays_it() {
    let mut state = state_with(mine(&["one", "two"]));
    assert_eq!(state.set_current_page(1, PLAYER), BookPageAction::Display);
    assert!(!state.request_pending());
    assert_eq!(state.current_page(), 1);
}

#[test]
fn turning_to_a_page_without_text_asks_and_gates_further_turns() {
    let mut state = state_with(vec![page(PLAYER, "one"), page_with(PLAYER, "", 0, 0)]);
    assert_eq!(
        state.set_current_page(1, PLAYER),
        BookPageAction::RequestPageText
    );
    assert!(state.request_pending());
    assert_eq!(state.set_current_page(0, PLAYER), BookPageAction::None);
    assert_eq!(state.current_page(), 1);
}

#[test]
fn one_past_the_end_asks_for_a_new_page() {
    let mut state = state_with(mine(&["one"]));
    assert_eq!(state.set_current_page(1, PLAYER), BookPageAction::AddPage);
    assert!(state.request_pending());
}

#[test]
fn more_than_one_past_the_end_is_refused() {
    let mut state = state_with(mine(&["one"]));
    assert_eq!(state.set_current_page(2, PLAYER), BookPageAction::None);
    assert_eq!(state.current_page(), 0);
}

#[test]
fn beyond_the_page_cap_is_refused() {
    let mut state = BookState::default();
    state.apply_open_book(&open_book(BOOK, 1, 500, mine(&["one"])), PLAYER);
    assert_eq!(state.set_current_page(1, PLAYER), BookPageAction::None);
}

#[test]
fn negative_or_unchanged_is_refused() {
    let mut state = state_with(mine(&["one"]));
    assert_eq!(state.set_current_page(-1, PLAYER), BookPageAction::None);
    assert_eq!(state.set_current_page(0, PLAYER), BookPageAction::None);
}

#[test]
fn with_no_book_open_a_turn_is_refused() {
    assert_eq!(
        BookState::default().set_current_page(0, PLAYER),
        BookPageAction::None
    );
}

#[test]
fn opening_clears_the_carried_page_before_turning() {
    let mut state = state_with(mine(&["one", "two"]));
    state.set_current_page(1, PLAYER);
    let turn = state.apply_open_book(
        &open_book(0x8000_9999, 8, 500, vec![page_with(OTHER, "", 0, 0)]),
        PLAYER,
    );
    assert_eq!(turn.action, BookPageAction::RequestPageText);
    assert_eq!(state.current_page(), 0);
}

#[test]
fn page_data_fills_the_page_and_lifts_the_gate() {
    let mut state = state_with(vec![page(PLAYER, "one"), page_with(PLAYER, "", 0, 0)]);
    state.set_current_page(1, PLAYER);
    assert!(state.apply_page_data(&data(BOOK, 1, page(OTHER, "fetched"))));
    assert!(!state.request_pending());
    assert_eq!(text(&state, 1), "fetched");
}

#[test]
fn page_data_for_another_page_moves_the_reader_and_keeps_the_gate() {
    let mut state = state_with(vec![
        page(PLAYER, "one"),
        page_with(PLAYER, "", 0, 0),
        page_with(PLAYER, "", 0, 0),
    ]);
    state.set_current_page(2, PLAYER);
    assert!(state.request_pending());
    assert!(state.apply_page_data(&data(BOOK, 1, page(OTHER, "late"))));
    assert_eq!(state.current_page(), 1);
    assert!(state.request_pending());
    assert_eq!(text(&state, 1), "");
    state.apply_page_data(&data(BOOK, 1, page(OTHER, "fetched")));
    assert!(!state.request_pending());
    assert_eq!(text(&state, 1), "fetched");
}

#[test]
fn page_data_for_another_book_is_dropped() {
    let mut state = state_with(mine(&["one"]));
    assert!(!state.apply_page_data(&data(0x8000_9999, 0, page(OTHER, "wrong"))));
    assert_eq!(text(&state, 0), "one");
}

#[test]
fn an_added_page_for_the_open_page_is_a_blank_page_of_the_player() {
    let mut state = state_with(mine(&["one"]));
    assert_eq!(state.set_current_page(1, PLAYER), BookPageAction::AddPage);
    assert!(state.apply_add_page_response(&added(1, true), PLAYER, "Acdream"));
    assert!(!state.request_pending());
    assert_eq!(state.pages().len(), 2);
    let new_page = state.page(1).unwrap();
    assert_eq!(
        (
            new_page.author_id,
            new_page.author_name.as_str(),
            new_page.page_text.as_str()
        ),
        (PLAYER, "Acdream", "")
    );
    assert!(state.is_page_editable(1, PLAYER));
}

#[test]
fn a_failed_add_asks_for_a_full_reread() {
    let mut state = state_with(mine(&["one"]));
    state.set_current_page(1, PLAYER);
    assert!(!state.apply_add_page_response(&added(1, false), PLAYER, "Acdream"));
}

#[test]
fn an_add_for_another_page_moves_there_and_asks_for_a_reread() {
    let mut state = state_with(mine(&["one"]));
    state.set_current_page(1, PLAYER);
    let before = state.revision();
    assert!(!state.apply_add_page_response(&added(5, true), PLAYER, "Acdream"));
    assert_eq!(state.current_page(), 5);
    assert!(state.revision() > before);
}

#[test]
fn a_page_deleted_locally_is_not_deleted_again() {
    let mut state = state_with(mine(&["one", "two", "three"]));
    state.set_current_page(1, PLAYER);
    let turn = state.turn_page(2, Some("  "), PLAYER);
    assert_eq!(turn.flush, BookFlushAction::DeletePage);
    assert_eq!(state.pages().len(), 2);
    assert_eq!((text(&state, 0), text(&state, 1)), ("one", "three"));
}

#[test]
fn editable_follows_the_author_and_the_ignore_author_flag() {
    let state = state_with(vec![
        page(PLAYER, "mine"),
        page(OTHER, "theirs"),
        page_with(OTHER, "communal", 1, 1),
    ]);
    assert!(state.is_page_editable(0, PLAYER));
    assert!(!state.is_page_editable(1, PLAYER));
    assert!(state.is_page_editable(2, PLAYER));
    assert!(!state.is_page_editable(9, PLAYER));
}

#[test]
fn a_flush_saves_text_on_a_page_the_reader_may_write() {
    let mut state = state_with(mine(&["old"]));
    assert_eq!(
        state.flush_current_page("new", PLAYER),
        (BookFlushAction::ModifyPage, 0)
    );
    assert_eq!(text(&state, 0), "new");
}

#[test]
fn a_flush_on_someone_else_s_page_sends_nothing() {
    let mut state = state_with(vec![page(OTHER, "theirs")]);
    assert_eq!(
        state.flush_current_page("edit", PLAYER).0,
        BookFlushAction::None
    );
    assert_eq!(text(&state, 0), "theirs");
}

#[test]
fn a_flush_on_a_communal_page_saves() {
    let mut state = state_with(vec![page_with(OTHER, "theirs", 1, 1)]);
    assert_eq!(
        state.flush_current_page("edit", PLAYER).0,
        BookFlushAction::ModifyPage
    );
}

#[test]
fn blanked_by_its_author_a_page_is_deleted() {
    let mut state = state_with(mine(&["one", "two"]));
    state.set_current_page(1, PLAYER);
    assert_eq!(
        state.flush_current_page("  \n ", PLAYER),
        (BookFlushAction::DeletePage, 1)
    );
    assert_eq!(state.pages().len(), 1);
}

#[test]
fn a_blanked_communal_page_by_another_is_saved_not_deleted() {
    let mut state = state_with(vec![page_with(OTHER, "theirs", 1, 1)]);
    assert_eq!(
        state.flush_current_page(" ", PLAYER).0,
        BookFlushAction::ModifyPage
    );
    assert_eq!(state.pages().len(), 1);
}

#[test]
fn a_flush_while_a_request_is_in_flight_sends_nothing() {
    let mut state = state_with(mine(&["one"]));
    state.mark_request_pending();
    assert_eq!(
        state.flush_current_page("edit", PLAYER).0,
        BookFlushAction::None
    );
    assert_eq!(text(&state, 0), "one");
}

#[test]
fn blank_is_spaces_newlines_and_terminators_only() {
    for t in ["", "   ", "\n\n"] {
        assert!(is_blank(t), "{t:?}");
    }
    for t in ["a", "  .  ", " \r "] {
        assert!(!is_blank(t), "{t:?}");
    }
}

#[test]
fn a_session_reset_closes_the_book() {
    let mut state = state_with(mine(&["one"]));
    state.mark_request_pending();
    state.reset_session();
    let view = state.view();
    assert!(!state.is_open());
    assert_eq!(
        (
            view.book_guid,
            view.pages.len(),
            view.current_page,
            view.scribe_id
        ),
        (0, 0, -1, 0)
    );
    assert!(view.inscription.is_empty() && !view.request_pending);
}

#[test]
fn a_reset_of_a_closed_book_changes_nothing() {
    let mut state = BookState::default();
    let before = state.revision();
    state.reset_session();
    assert_eq!(state.revision(), before);
}

#[test]
fn the_revision_moves_on_every_accepted_change() {
    let mut state = BookState::default();
    let start = state.revision();
    state.apply_open_book(&open(mine(&["one", "two"])), PLAYER);
    let opened = state.revision();
    assert!(opened > start);
    state.set_current_page(1, PLAYER);
    assert!(state.revision() > opened);
}

#[test]
fn a_turn_saves_what_was_typed_on_the_page_being_left() {
    let mut state = state_with(mine(&["old", "two"]));
    let turn = state.turn_page(1, Some("edited"), PLAYER);
    assert_eq!(
        (turn.flush, turn.flush_page, turn.action, turn.page),
        (BookFlushAction::ModifyPage, 0, BookPageAction::Display, 1)
    );
    assert_eq!(text(&state, 0), "edited");
}

#[test]
fn a_turn_without_text_saves_nothing() {
    let mut state = state_with(mine(&["old", "two"]));
    assert_eq!(
        state.turn_page(1, None, PLAYER).flush,
        BookFlushAction::None
    );
    assert_eq!(text(&state, 0), "old");
}

#[test]
fn forward_off_a_deleted_page_follows_the_pages_that_shifted() {
    let mut state = state_with(mine(&["one", "two", "three"]));
    state.set_current_page(1, PLAYER);
    let turn = state.turn_page(2, Some("   "), PLAYER);
    assert_eq!(
        (turn.flush, turn.flush_page, turn.page, turn.action),
        (BookFlushAction::DeletePage, 1, 1, BookPageAction::Display)
    );
    assert_eq!(text(&state, 1), "three");
    assert_eq!(state.pages().len(), 2);
}

#[test]
fn backward_off_a_deleted_page_does_not_shift() {
    let mut state = state_with(mine(&["one", "two", "three"]));
    state.set_current_page(1, PLAYER);
    let turn = state.turn_page(0, Some(""), PLAYER);
    assert_eq!((turn.flush, turn.page), (BookFlushAction::DeletePage, 0));
    assert_eq!(text(&state, 0), "one");
}

#[test]
fn a_turn_to_the_same_page_is_refused_without_saving() {
    let mut state = state_with(mine(&["one"]));
    let turn = state.turn_page(0, Some("edited"), PLAYER);
    assert_eq!(
        (turn.flush, turn.action),
        (BookFlushAction::None, BookPageAction::None)
    );
    assert_eq!(text(&state, 0), "one");
}

#[test]
fn a_turn_while_a_request_is_in_flight_goes_nowhere() {
    let mut state = state_with(mine(&["one", "two"]));
    state.mark_request_pending();
    let turn = state.turn_page(1, Some("edited"), PLAYER);
    assert_eq!(
        (turn.flush, turn.action),
        (BookFlushAction::None, BookPageAction::None)
    );
    assert_eq!(state.current_page(), 0);
    assert_eq!(text(&state, 0), "one");
}

#[test]
fn an_inscription_retitles_the_book_without_touching_the_pages() {
    let mut state = state_with(mine(&["one"]));
    assert!(state.apply_inscription(&inscription(BOOK, "Newly Inscribed")));
    let view = state.view();
    assert_eq!(
        (
            view.inscription.as_str(),
            view.scribe_id,
            view.scribe_name.as_str()
        ),
        ("Newly Inscribed", PLAYER, "Acdream")
    );
    assert_eq!(text(&state, 0), "one");
}

#[test]
fn an_inscription_for_another_object_is_dropped() {
    let mut state = state_with(mine(&["one"]));
    assert!(!state.apply_inscription(&inscription(0x8000_9999, "Not this one")));
    assert_eq!(state.view().inscription, "An Inscription");
    assert!(!BookState::default().apply_inscription(&inscription(BOOK, "text")));
}

#[test]
fn past_a_blank_last_page_you_wrote_the_turn_is_refused() {
    let mut state = state_with(mine(&["one", "two"]));
    state.set_current_page(1, PLAYER);
    let turn = state.turn_page(2, Some("   "), PLAYER);
    assert_eq!(
        (turn.action, turn.flush),
        (BookPageAction::RefusedLastPageBlank, BookFlushAction::None)
    );
    assert_eq!((state.current_page(), state.pages().len()), (1, 2));
    assert!(!state.request_pending());
}

#[test]
fn back_off_a_blank_last_page_still_deletes_it() {
    let mut state = state_with(mine(&["one", "two"]));
    state.set_current_page(1, PLAYER);
    assert_eq!(
        state.turn_page(0, Some("   "), PLAYER).flush,
        BookFlushAction::DeletePage
    );
    assert_eq!(state.pages().len(), 1);
}

#[test]
fn past_a_blank_last_page_someone_else_wrote_the_turn_adds_a_page() {
    let mut state = state_with(vec![page(PLAYER, "one"), page_with(OTHER, "", 1, 1)]);
    state.set_current_page(1, PLAYER);
    assert_eq!(
        state.turn_page(2, Some(""), PLAYER).action,
        BookPageAction::AddPage
    );
}

#[test]
fn a_turn_off_someone_else_s_page_saves_nothing() {
    let mut state = state_with(vec![page(OTHER, "theirs"), page(OTHER, "two")]);
    let turn = state.turn_page(1, Some("vandalism"), PLAYER);
    assert_eq!(
        (turn.flush, turn.action),
        (BookFlushAction::None, BookPageAction::Display)
    );
    assert_eq!(text(&state, 0), "theirs");
}
