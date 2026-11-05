//! Books and inscriptions: the open-book and page events, and the requests
//! an open book sends. A book is opened by a plain use of the item.
//!
//! Game-action builders take the session's next action sequence number
//! and are sent on the UI queue.

use ac_bits::Reader;

use crate::wire::{action, check_count, read_string, u32_action};
use crate::{GameActionType, Result};

/// Marks a page record that carries its text-included and ignore-author
/// words explicitly; any other value is an older record whose one word is
/// the text-included flag.
const EXPLICIT_PAGE_FLAGS: u32 = 0xFFFF_0002;

/// One page as it arrives.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BookPage {
    /// Who wrote it.
    pub author_id: u32,
    /// The author's name.
    pub author_name: String,
    /// The author's account.
    pub author_account: String,
    /// Zero when the text was left out and must be fetched separately.
    pub text_included: u32,
    /// Set on books anyone may write in: the page is editable even though
    /// the player did not write it.
    pub ignore_author: u32,
    /// The text (empty when not included).
    pub page_text: String,
}

impl BookPage {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        let author_id = r.u32()?;
        let author_name = read_string(r)?;
        let author_account = read_string(r)?;
        let marker = r.u32()?;
        let (text_included, ignore_author) = if marker == EXPLICIT_PAGE_FLAGS {
            (r.u32()?, r.u32()?)
        } else {
            (marker, 0)
        };
        let page_text = if text_included != 0 {
            read_string(r)?
        } else {
            String::new()
        };
        Ok(Self {
            author_id,
            author_name,
            author_account,
            text_included,
            ignore_author,
            page_text,
        })
    }
}

/// The pages of an open book, with the caps the list repeats.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BookPageList {
    /// Page cap.
    pub max_num_pages: i32,
    /// Characters allowed per page.
    pub max_num_chars_per_page: i32,
    /// The pages.
    pub pages: Vec<BookPage>,
}

impl BookPageList {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        let max_num_pages = r.i32()?;
        let max_num_chars_per_page = r.i32()?;
        let count = r.i32()?;
        let mut pages = Vec::new();
        if count > 0 {
            // A page takes at least an id, two string lengths and a flag.
            check_count(r, count as u32, 12, "book pages")?;
            for _ in 0..count {
                pages.push(BookPage::read(r)?);
            }
        }
        Ok(Self {
            max_num_pages,
            max_num_chars_per_page,
            pages,
        })
    }
}

/// A book opened ([`crate::GameEventType::BOOK_DATA_RESPONSE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OpenBook {
    /// The book.
    pub book_guid: u32,
    /// Page cap.
    pub max_num_pages: i32,
    /// The pages.
    pub pages: BookPageList,
    /// The book's inscription.
    pub inscription: String,
    /// Who inscribed it.
    pub scribe_id: u32,
    /// Their name.
    pub scribe_name: String,
}

impl OpenBook {
    /// Parse the event payload: book, page cap, the page list,
    /// inscription, scribe id, scribe name.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            book_guid: r.u32()?,
            max_num_pages: r.i32()?,
            pages: BookPageList::read(&mut r)?,
            inscription: read_string(&mut r)?,
            scribe_id: r.u32()?,
            scribe_name: read_string(&mut r)?,
        })
    }
}

/// One page's text ([`crate::GameEventType::BOOK_PAGE_DATA_RESPONSE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PageDataResponse {
    /// The book.
    pub book_guid: u32,
    /// The page number.
    pub page_number: i32,
    /// The page.
    pub page: BookPage,
}

impl PageDataResponse {
    /// Parse the event payload: book, page number, page.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            book_guid: r.u32()?,
            page_number: r.i32()?,
            page: BookPage::read(&mut r)?,
        })
    }
}

/// An item's inscription
/// ([`crate::GameEventType::GET_INSCRIPTION_RESPONSE`]). Current servers
/// do not send it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Inscription {
    /// The item.
    pub object_guid: u32,
    /// The inscription.
    pub text: String,
    /// Who inscribed it.
    pub scribe_id: u32,
    /// Their name.
    pub scribe_name: String,
    /// Their account; empty when the payload stops before it.
    pub scribe_account: String,
}

impl Inscription {
    /// Parse the event payload: item, text, scribe id, scribe name, and
    /// the scribe's account when at least two bytes remain.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let object_guid = r.u32()?;
        let text = read_string(&mut r)?;
        let scribe_id = r.u32()?;
        let scribe_name = read_string(&mut r)?;
        let scribe_account = if r.remaining() >= 2 {
            read_string(&mut r)?
        } else {
            String::new()
        };
        Ok(Self {
            object_guid,
            text,
            scribe_id,
            scribe_name,
            scribe_account,
        })
    }
}

/// The answer to an add-page, delete-page or modify-page request
/// ([`crate::GameEventType::BOOK_ADD_PAGE_RESPONSE`],
/// [`crate::GameEventType::BOOK_DELETE_PAGE_RESPONSE`],
/// [`crate::GameEventType::BOOK_MODIFY_PAGE_RESPONSE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PageResponse {
    /// The book.
    pub book_guid: u32,
    /// The page.
    pub page_number: i32,
    /// It worked.
    pub success: bool,
}

impl PageResponse {
    /// Parse the event payload: book, page, success word.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            book_guid: r.u32()?,
            page_number: r.i32()?,
            success: r.u32()? != 0,
        })
    }
}

/// Ask for the whole book again.
#[must_use]
pub fn build_book_data(sequence: u32, book_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::BOOK_DATA, book_guid)
}

/// Add a page.
#[must_use]
pub fn build_book_add_page(sequence: u32, book_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::BOOK_ADD_PAGE, book_guid)
}

/// Ask for one page's text: book, then page.
#[must_use]
pub fn build_book_page_data(sequence: u32, book_guid: u32, page: i32) -> Vec<u8> {
    book_page_action(sequence, GameActionType::BOOK_PAGE_DATA, book_guid, page)
}

/// Delete a page: book, then page.
#[must_use]
pub fn build_book_delete_page(sequence: u32, book_guid: u32, page: i32) -> Vec<u8> {
    book_page_action(sequence, GameActionType::BOOK_DELETE_PAGE, book_guid, page)
}

/// Rewrite a page: book, page, then the text.
pub fn build_book_modify_page(
    sequence: u32,
    book_guid: u32,
    page: i32,
    text: &str,
) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::BOOK_MODIFY_PAGE);
    w.u32(book_guid);
    w.i32(page);
    w.string16l(text)?;
    Ok(w.into_vec())
}

fn book_page_action(
    sequence: u32,
    action_type: GameActionType,
    book_guid: u32,
    page: i32,
) -> Vec<u8> {
    let mut w = action(sequence, action_type);
    w.u32(book_guid);
    w.i32(page);
    w.into_vec()
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/BookMessagesTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    fn page(
        w: &mut Writer,
        author: u32,
        name: &str,
        account: &str,
        ignore: bool,
        text: Option<&str>,
    ) {
        w.u32(author);
        w.string16l(name).unwrap();
        w.string16l(account).unwrap();
        w.u32(EXPLICIT_PAGE_FLAGS);
        w.i32(i32::from(text.is_some()));
        w.i32(i32::from(ignore));
        if let Some(text) = text {
            w.string16l(text).unwrap();
        }
    }

    fn two_page_book() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x8000_1234);
        for v in [8, 8, 1000, 2] {
            w.i32(v);
        }
        page(
            &mut w,
            0x5000_0001,
            "Scribbler",
            "acct",
            false,
            Some("first"),
        );
        page(&mut w, 0x5000_0002, "Other", "acct2", true, None);
        w.string16l("A Traveller's Log").unwrap();
        w.u32(0x5000_0001);
        w.string16l("Scribbler!").unwrap();
        w.into_vec()
    }

    #[test]
    fn open_book_reads_every_field() {
        let book = OpenBook::parse(&two_page_book()).unwrap();
        assert_eq!(book.book_guid, 0x8000_1234);
        assert_eq!(book.max_num_pages, 8);
        assert_eq!(book.pages.max_num_pages, 8);
        assert_eq!(book.pages.max_num_chars_per_page, 1000);
        assert_eq!(book.pages.pages.len(), 2);
        let first = &book.pages.pages[0];
        assert_eq!(first.author_id, 0x5000_0001);
        assert_eq!(first.author_name, "Scribbler");
        assert_eq!(first.author_account, "acct");
        assert_ne!(first.text_included, 0);
        assert_eq!(first.ignore_author, 0);
        assert_eq!(first.page_text, "first");
        let second = &book.pages.pages[1];
        assert_eq!(second.text_included, 0);
        assert_eq!(second.ignore_author, 1);
        assert_eq!(second.page_text, "");
        assert_eq!(book.inscription, "A Traveller's Log");
        assert_eq!(book.scribe_id, 0x5000_0001);
        assert_eq!(book.scribe_name, "Scribbler!");
        assert_every_prefix_fails(&two_page_book(), OpenBook::parse);
    }

    #[test]
    fn empty_and_legacy_books() {
        let mut w = Writer::new();
        w.u32(0x8000_AAAA);
        for v in [4, 4, 500, 0] {
            w.i32(v);
        }
        w.string16l("").unwrap();
        w.u32(0);
        w.string16l("").unwrap();
        let book = OpenBook::parse(w.as_slice()).unwrap();
        assert!(book.pages.pages.is_empty());
        assert_eq!(book.scribe_id, 0);

        // A record without the marker: its one word is the text flag.
        let mut w = Writer::new();
        w.u32(0x8000_BBBB);
        for v in [1, 1, 500, 1] {
            w.i32(v);
        }
        w.u32(0x5000_0009);
        w.string16l("Author").unwrap();
        w.string16l("acct").unwrap();
        w.u32(1);
        w.string16l("legacy page").unwrap();
        w.string16l("").unwrap();
        w.u32(0);
        w.string16l("").unwrap();
        let book = OpenBook::parse(w.as_slice()).unwrap();
        let page = &book.pages.pages[0];
        assert_eq!((page.text_included, page.ignore_author), (1, 0));
        assert_eq!(page.page_text, "legacy page");

        // Claims three pages and stops.
        assert!(OpenBook::parse(&words(&[0x8000_CCCC, 4, 4, 500, 3])).is_err());
    }

    #[test]
    fn page_data_and_page_responses() {
        let mut w = Writer::new();
        w.u32(0x8000_5555);
        w.i32(2);
        page(
            &mut w,
            0x5000_0003,
            "Writer",
            "acct",
            true,
            Some("page three"),
        );
        let parsed = PageDataResponse::parse(w.as_slice()).unwrap();
        assert_eq!(parsed.book_guid, 0x8000_5555);
        assert_eq!(parsed.page_number, 2);
        assert_eq!(parsed.page.page_text, "page three");
        assert_eq!(parsed.page.ignore_author, 1);
        assert_eq!(parsed.page.author_name, "Writer");
        assert_every_prefix_fails(w.as_slice(), PageDataResponse::parse);

        for (word, success) in [(1, true), (0, false)] {
            assert_eq!(
                PageResponse::parse(&words(&[0x8000_7777, 3, word])),
                Ok(PageResponse {
                    book_guid: 0x8000_7777,
                    page_number: 3,
                    success
                })
            );
        }
        assert!(PageResponse::parse(&[0; 8]).is_err());
    }

    #[test]
    fn inscription_with_and_without_account() {
        let mut w = Writer::new();
        w.u32(0x8000_4321);
        w.string16l("Property of nobody").unwrap();
        w.u32(0x5000_000A);
        w.string16l("Acdream").unwrap();
        w.string16l("testaccount").unwrap();
        let parsed = Inscription::parse(w.as_slice()).unwrap();
        assert_eq!(parsed.object_guid, 0x8000_4321);
        assert_eq!(parsed.text, "Property of nobody");
        assert_eq!(parsed.scribe_id, 0x5000_000A);
        assert_eq!(parsed.scribe_name, "Acdream");
        assert_eq!(parsed.scribe_account, "testaccount");

        let mut w = Writer::new();
        w.u32(0x8000_4321);
        w.string16l("Inscribed").unwrap();
        w.u32(0);
        w.string16l("").unwrap();
        assert_eq!(Inscription::parse(w.as_slice()).unwrap().scribe_account, "");
        assert!(Inscription::parse(&[0; 3]).is_err());
    }

    #[test]
    fn requests() {
        assert_eq!(
            build_book_data(0x11, 0x8000_1234),
            words(&[0xF7B1, 0x11, 0xAA, 0x8000_1234])
        );
        assert_eq!(
            build_book_add_page(0x12, 0x8000_1234),
            words(&[0xF7B1, 0x12, 0xAC, 0x8000_1234])
        );
        assert_eq!(
            build_book_page_data(0x13, 0x8000_1234, 4),
            words(&[0xF7B1, 0x13, 0xAE, 0x8000_1234, 4])
        );
        assert_eq!(
            build_book_delete_page(0x14, 0x8000_1234, 1),
            words(&[0xF7B1, 0x14, 0xAD, 0x8000_1234, 1])
        );
        let body = build_book_modify_page(0x15, 0x8000_1234, 0, "hi").unwrap();
        assert_eq!(body.len(), 24);
        assert_eq!(body[..20], words(&[0xF7B1, 0x15, 0xAB, 0x8000_1234, 0]));
        assert_eq!(body[20..], [2, 0, b'h', b'i']);
        let body = build_book_modify_page(0x16, 1, 0, "abc").unwrap();
        assert_eq!(body.len(), 28);
        assert_eq!(body[25..], [0, 0, 0]);
        let body = build_book_modify_page(0x17, 0xDEAD_BEEF, 7, "text").unwrap();
        assert_eq!(body[16..20], 7i32.to_le_bytes());
        assert_eq!(body[22..26], *b"text");
    }
}
