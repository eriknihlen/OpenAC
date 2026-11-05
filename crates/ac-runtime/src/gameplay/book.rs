//! The one open book: opened by the server's answer to a use, read and
//! written page by page, closed when the reader is done or the session
//! ends. Turning a page says what to send: the page being left is saved or
//! deleted, the page being opened is fetched or added.

use ac_proto::book::{BookPage, Inscription, OpenBook, PageDataResponse, PageResponse};

/// What turning to a page asks the caller to do next.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum BookPageAction {
    /// Refused: no book, a request in flight, out of range, or already
    /// the open page.
    #[default]
    None,
    /// The page's text is here; show it.
    Display,
    /// The page exists without its text: ask for it.
    RequestPageText,
    /// There is no page there yet: ask for one to be added.
    AddPage,
    /// The reader is on the last page, it is blank, and they wrote it: the
    /// turn does not happen.
    RefusedLastPageBlank,
}

/// What closing the open page asks the caller to send.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum BookFlushAction {
    /// Nothing.
    #[default]
    None,
    /// Save the page's text.
    ModifyPage,
    /// The reader blanked a page they wrote: delete it.
    DeletePage,
}

/// One page turn: what the page being left sends, and what the page being
/// opened needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct BookPageTurn {
    /// What the page being left sends.
    pub flush: BookFlushAction,
    /// The page being left.
    pub flush_page: i32,
    /// What the page being opened needs.
    pub action: BookPageAction,
    /// The page being opened.
    pub page: i32,
}

/// The open book as a host shows it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BookView {
    /// Counts changes.
    pub revision: u64,
    /// The book (0 when none is open).
    pub book_guid: u32,
    /// Page cap.
    pub max_num_pages: i32,
    /// Characters per page.
    pub max_num_chars_per_page: i32,
    /// The pages in hand.
    pub pages: Vec<BookPage>,
    /// The page being read (-1 for none).
    pub current_page: i32,
    /// The inscription.
    pub inscription: String,
    /// Who inscribed it (id).
    pub scribe_id: u32,
    /// Who inscribed it.
    pub scribe_name: String,
    /// A request is in flight.
    pub request_pending: bool,
}

/// The open book.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BookState {
    book_guid: u32,
    max_num_pages: i32,
    max_num_chars_per_page: i32,
    pages: Vec<BookPage>,
    current_page: i32,
    inscription: String,
    scribe_id: u32,
    scribe_name: String,
    request_pending: bool,
    revision: u64,
}

impl Default for BookState {
    fn default() -> Self {
        Self {
            book_guid: 0,
            max_num_pages: 0,
            max_num_chars_per_page: 0,
            pages: Vec::new(),
            current_page: -1,
            inscription: String::new(),
            scribe_id: 0,
            scribe_name: String::new(),
            request_pending: false,
            revision: 0,
        }
    }
}

impl BookState {
    /// The open book (0 for none).
    #[must_use]
    pub fn book_guid(&self) -> u32 {
        self.book_guid
    }

    /// A book is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.book_guid != 0
    }

    /// The page being read (-1 for none).
    #[must_use]
    pub fn current_page(&self) -> i32 {
        self.current_page
    }

    /// The pages in hand.
    #[must_use]
    pub fn pages(&self) -> &[BookPage] {
        &self.pages
    }

    /// One page.
    #[must_use]
    pub fn page(&self, index: i32) -> Option<&BookPage> {
        usize::try_from(index).ok().and_then(|i| self.pages.get(i))
    }

    /// A request is in flight.
    #[must_use]
    pub fn request_pending(&self) -> bool {
        self.request_pending
    }

    /// Counts changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The page may be typed into: `player` wrote it, or anyone may.
    #[must_use]
    pub fn is_page_editable(&self, index: i32, player: u32) -> bool {
        self.page(index).is_some_and(|p| editable(p, player))
    }

    /// The open book as owned data.
    #[must_use]
    pub fn view(&self) -> BookView {
        BookView {
            revision: self.revision,
            book_guid: self.book_guid,
            max_num_pages: self.max_num_pages,
            max_num_chars_per_page: self.max_num_chars_per_page,
            pages: self.pages.clone(),
            current_page: self.current_page,
            inscription: self.inscription.clone(),
            scribe_id: self.scribe_id,
            scribe_name: self.scribe_name.clone(),
            request_pending: self.request_pending,
        }
    }

    /// The server opened a book. Re-opening the same book keeps the page
    /// the reader was on when it still exists; otherwise the first page.
    /// Opening ends in a page turn, which may ask for a page's text or a
    /// first page.
    pub fn apply_open_book(&mut self, book: &OpenBook, player: u32) -> BookPageTurn {
        let carried = if self.book_guid == book.book_guid {
            self.current_page
        } else {
            0
        };
        self.book_guid = book.book_guid;
        self.max_num_pages = book.max_num_pages;
        self.max_num_chars_per_page = book.pages.max_num_chars_per_page;
        self.pages.clone_from(&book.pages.pages);
        self.inscription.clone_from(&book.inscription);
        self.scribe_id = book.scribe_id;
        self.scribe_name.clone_from(&book.scribe_name);
        self.request_pending = false;
        let last = page_count(&self.pages) - 1;
        let page = if last >= carried { carried } else { last }.max(0);
        // The book that was open is closed first, so no page is current and
        // the turn always has somewhere to go.
        self.current_page = -1;
        self.bump();
        self.turn_page(page, None, player)
    }

    /// One page's text arrived. For the page being read it is stored and
    /// lifts the request gate; for another page the reader moves there and
    /// the gate stays. Another book's answer is dropped (false).
    pub fn apply_page_data(&mut self, response: &PageDataResponse) -> bool {
        if self.book_guid == 0 || response.book_guid != self.book_guid {
            return false;
        }
        if response.page_number == self.current_page {
            self.set_page(response.page_number, response.page.clone());
            self.request_pending = false;
        } else {
            self.current_page = response.page_number;
        }
        self.bump();
        true
    }

    /// An add-page answer. Success for the page being read becomes a blank
    /// page by the player; anything else needs the whole book read again,
    /// which a false return asks for.
    pub fn apply_add_page_response(
        &mut self,
        response: &PageResponse,
        player: u32,
        author_name: &str,
    ) -> bool {
        if self.book_guid == 0 || response.book_guid != self.book_guid {
            return true;
        }
        if !response.success {
            return false;
        }
        if response.page_number != self.current_page {
            self.current_page = response.page_number;
            self.bump();
            return false;
        }
        self.set_page(
            response.page_number,
            BookPage {
                author_id: player,
                author_name: author_name.to_owned(),
                author_account: String::new(),
                text_included: 1,
                ignore_author: 0,
                page_text: String::new(),
            },
        );
        self.request_pending = false;
        self.bump();
        true
    }

    /// An inscription for the open book retitles it.
    pub fn apply_inscription(&mut self, inscription: &Inscription) -> bool {
        if self.book_guid == 0 || inscription.object_guid != self.book_guid {
            return false;
        }
        self.inscription.clone_from(&inscription.text);
        self.scribe_id = inscription.scribe_id;
        self.scribe_name.clone_from(&inscription.scribe_name);
        self.bump();
        true
    }

    // The delete-page and modify-page answers are not folded in: the page
    // was changed locally when the request went out, and folding the answer
    // in would delete a second page.

    /// Turn to a page without saving.
    pub fn set_current_page(&mut self, page: i32, player: u32) -> BookPageAction {
        self.turn_page(page, None, player).action
    }

    /// Turn a page, saving what the reader typed on the page being left
    /// (`None` saves nothing). The request gate goes up for the two answers
    /// that need one.
    pub fn turn_page(
        &mut self,
        requested: i32,
        page_text: Option<&str>,
        player: u32,
    ) -> BookPageTurn {
        if self.book_guid == 0 || self.request_pending {
            return BookPageTurn::default();
        }
        if requested < 0 || requested >= self.max_num_pages {
            return BookPageTurn::default();
        }
        if requested == self.current_page || requested > page_count(&self.pages) {
            return BookPageTurn::default();
        }
        // Appending after a blank page the reader wrote is refused: that
        // empty page already is the new page.
        if self.current_page >= 0
            && self.current_page == page_count(&self.pages) - 1
            && requested > self.current_page
            && let Some(current) = self.page(self.current_page)
            && is_blank(page_text.unwrap_or(&current.page_text))
            && current.author_id == player
        {
            return BookPageTurn {
                flush: BookFlushAction::None,
                flush_page: self.current_page,
                action: BookPageAction::RefusedLastPageBlank,
                page: self.current_page,
            };
        }
        let flush_page = self.current_page;
        let flush = match page_text {
            Some(text) => self.flush_current_page(text, player).0,
            None => BookFlushAction::None,
        };
        let mut page = requested;
        if flush == BookFlushAction::DeletePage && page > self.current_page {
            page -= 1;
        }
        self.current_page = page;
        self.bump();
        let action = match self.page(page) {
            None => {
                self.request_pending = true;
                BookPageAction::AddPage
            }
            Some(p) if p.text_included == 0 => {
                self.request_pending = true;
                BookPageAction::RequestPageText
            }
            Some(_) => BookPageAction::Display,
        };
        BookPageTurn {
            flush,
            flush_page,
            action,
            page,
        }
    }

    /// Close the open page with what the reader typed: returns what to send
    /// and the page. A page the player may not write, or one with a request
    /// in flight, sends nothing.
    pub fn flush_current_page(&mut self, text: &str, player: u32) -> (BookFlushAction, i32) {
        let page = self.current_page;
        if self.book_guid == 0 || self.request_pending {
            return (BookFlushAction::None, page);
        }
        let Some(index) = usize::try_from(page).ok().filter(|&i| i < self.pages.len()) else {
            return (BookFlushAction::None, page);
        };
        let current = &self.pages[index];
        if !editable(current, player) {
            return (BookFlushAction::None, page);
        }
        if is_blank(text) && current.author_id == player {
            // The page being read keeps its index: a turn that follows
            // adjusts against it.
            self.pages.remove(index);
            self.bump();
            return (BookFlushAction::DeletePage, page);
        }
        let current = &mut self.pages[index];
        current.page_text = text.to_owned();
        current.text_included = 1;
        self.bump();
        (BookFlushAction::ModifyPage, page)
    }

    /// Raise the gate for a request sent outside a page turn (the whole-book
    /// re-read).
    pub fn mark_request_pending(&mut self) {
        if self.book_guid == 0 {
            return;
        }
        self.request_pending = true;
        self.bump();
    }

    /// Close the book.
    pub fn close_book(&mut self) {
        if self.book_guid == 0
            && self.pages.is_empty()
            && self.current_page == -1
            && !self.request_pending
        {
            return;
        }
        let revision = self.revision + 1;
        *self = Self {
            revision,
            ..Self::default()
        };
    }

    /// The session ended.
    pub fn reset_session(&mut self) {
        self.close_book();
    }

    fn set_page(&mut self, index: i32, page: BookPage) {
        let Ok(index) = usize::try_from(index) else {
            return;
        };
        while self.pages.len() < index {
            self.pages.push(BookPage {
                author_id: 0,
                author_name: String::new(),
                author_account: String::new(),
                text_included: 0,
                ignore_author: 0,
                page_text: String::new(),
            });
        }
        if index < self.pages.len() {
            self.pages[index] = page;
        } else {
            self.pages.push(page);
        }
    }

    fn bump(&mut self) {
        self.revision += 1;
    }
}

fn page_count(pages: &[BookPage]) -> i32 {
    i32::try_from(pages.len()).unwrap_or(i32::MAX)
}

fn editable(page: &BookPage, player: u32) -> bool {
    page.ignore_author != 0 || page.author_id == player
}

/// A page is blank when every character is a space, a newline or a
/// terminator.
#[must_use]
pub fn is_blank(text: &str) -> bool {
    text.chars().all(|c| matches!(c, ' ' | '\n' | '\0'))
}

#[cfg(test)]
#[path = "book_tests.rs"]
mod tests;
