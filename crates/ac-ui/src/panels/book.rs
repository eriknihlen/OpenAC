//! The book reader (layout 0x2100006E, slot 0x10000182): one of the main
//! panel's slots. It shows itself when the session opens a book (the
//! server's answer to a use), shows the open page's text, its title and a
//! menu of every page the book can hold, and turns pages with its buttons
//! and menu, saving what the reader typed on the page being left.
//!
//! The book is the session's ([`BookView`]); the reader asks for turns and
//! the close with [`BookCommand`]s and shows the next view.

use ac_proto::book::BookPage;
use ac_runtime::gameplay::{BookCommand, BookView, GameplayCommand};
use ac_runtime::session::SessionCommand;

use crate::element_info::{ElementInfo, StateInfo};
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, panel_id};
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Field, Menu, MenuItem, ScrollSource, Scrollbar, Text};

/// The layout hosting the main panels.
pub const HOST_LAYOUT_ID: u32 = 0x2100_006E;
/// The book reader's slot in it.
pub const SLOT_ELEMENT_ID: u32 = 0x1000_0182;
/// The title.
pub const TITLE_TEXT_ID: u32 = 0x1000_010F;
/// The page text.
pub const PAGE_TEXT_ID: u32 = 0x1000_0111;
/// The previous-page button.
pub const PREVIOUS_BUTTON_ID: u32 = 0x1000_0114;
/// The next-page button.
pub const NEXT_BUTTON_ID: u32 = 0x1000_0115;
/// The page menu.
pub const PAGE_MENU_ID: u32 = 0x1000_0470;
/// The page menu's face text.
pub const PAGE_NUMBER_TEXT_ID: u32 = 0x1000_047B;
/// The close button (top right of the parchment).
pub const CLOSE_BUTTON_ID: u32 = 0x1000_010E;
/// The bar beside the page text (authored as the text's own child).
pub const PAGE_SCROLLBAR_ID: u32 = 0x1000_048A;
/// Rows of the page menu shown before it scrolls.
pub const VISIBLE_PAGE_ROWS: usize = 7;

/// The authored flag that makes a text element take typing.
const EDITABLE_PROPERTY_ID: u32 = 0x16;

/// The popup's scrollbar art.
const POPUP_TRACK: u32 = 0x0600_4C5F;
const POPUP_THUMB_TOP: u32 = 0x0600_4C60;
const POPUP_THUMB: u32 = 0x0600_4C63;
const POPUP_THUMB_BOTTOM: u32 = 0x0600_4C66;
const POPUP_UP: u32 = 0x0600_4C6C;
const POPUP_DOWN: u32 = 0x0600_4C69;

/// What an unauthored page is listed as.
pub const UNAUTHORED_PAGE_LABEL: &str = "<prewritten>";

/// How a page-list entry reads: "Page N  - ", then "by Name " when the
/// page names its writer, then "<account>" for a reader the server tells
/// the truth about accounts to (an admin or the like).
#[must_use]
pub fn page_label(
    page_index: i32,
    author_name: Option<&str>,
    author_account: Option<&str>,
    shows_account: bool,
) -> String {
    let mut label = format!("Page {}  - ", page_index + 1);
    if let Some(name) = author_name.filter(|n| !n.trim().is_empty()) {
        label.push_str(&format!("by {name} "));
    }
    if shows_account && let Some(account) = author_account.filter(|a| !a.trim().is_empty()) {
        label.push_str(&format!("<{account}>"));
    }
    label
}

/// Make the page text of the slot description `root` take typing, so the
/// built element can be switched between read only and typeable (call
/// before building).
pub fn mark_page_text_typeable(root: &mut ElementInfo) {
    fn find(info: &mut ElementInfo, id: u32) -> Option<&mut ElementInfo> {
        if info.id == id {
            return Some(info);
        }
        info.children.iter_mut().find_map(|c| find(c, id))
    }
    let Some(page) = find(root, PAGE_TEXT_ID) else {
        return;
    };
    let mut direct = page
        .state(ac_dat::objects::DIRECT_STATE_ID)
        .cloned()
        .unwrap_or_else(|| StateInfo::named(ac_dat::objects::DIRECT_STATE_ID, ""));
    direct.properties.set_value(
        EDITABLE_PROPERTY_ID,
        ac_dat::objects::PropertyValue::Bool(true),
    );
    page.set_state(ac_dat::objects::DIRECT_STATE_ID, direct);
}

/// What the reader reads besides the book.
#[derive(Debug, Clone, Copy, Default)]
pub struct BookContext<'a> {
    /// The player's object id (who may write a page).
    pub player: u32,
    /// The book item's displayed name (an uninscribed book's title).
    pub book_name: &'a str,
    /// The player may see the writers' accounts.
    pub shows_account: bool,
}

/// The book reader's controller.
#[derive(Debug)]
pub struct BookPanelController {
    root: ElementId,
    title: Option<ElementId>,
    page_text: ElementId,
    page_field: bool,
    page_number: Option<ElementId>,
    page_menu: Option<ElementId>,
    previous: Option<ElementId>,
    next: Option<ElementId>,
    close: Option<ElementId>,
    view: BookView,
    player: u32,
    shows_account: bool,
    rendered_revision: Option<u64>,
    was_open: bool,
    close_sent: Option<u32>,
}

fn set_text(ui: &mut UiRoot, id: Option<ElementId>, s: &str) {
    if let Some(t) = id.and_then(|id| ui.widget_mut::<Text>(id)) {
        let color = t.default_color;
        t.set_lines(if s.is_empty() {
            Vec::new()
        } else {
            vec![Line::new(s, color)]
        });
    }
}

fn editable(page: &BookPage, player: u32) -> bool {
    page.ignore_author != 0 || page.author_id == player
}

impl BookPanelController {
    /// Bind to the imported slot (`None` without its page text).
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Option<Self> {
        let root = layout.root;
        let page_text = ui.find_descendant(root, PAGE_TEXT_ID)?;
        let page_field = ui.widget::<Field>(page_text).is_some();
        if !page_field && ui.widget::<Text>(page_text).is_none() {
            return None;
        }
        let text = |ui: &UiRoot, id| {
            ui.find_descendant(root, id)
                .filter(|t| ui.widget::<Text>(*t).is_some())
        };
        let title = text(ui, TITLE_TEXT_ID);
        let page_number = text(ui, PAGE_NUMBER_TEXT_ID);
        let page_menu = ui
            .find_descendant(root, PAGE_MENU_ID)
            .filter(|m| ui.widget::<Menu>(*m).is_some());
        if let Some(m) = page_menu
            && let Some(w) = ui.widget_mut::<Menu>(m)
        {
            w.scrollable = true;
            w.popup_size_to_content = false;
            w.popup_scrollbar_hide_when_disabled = true;
            w.open_upward = false;
            w.rows_per_column = VISIBLE_PAGE_ROWS;
            w.item_normal_sprite = w.normal_sprite;
            w.item_highlight_sprite = w.pressed_sprite;
            w.popup_bg_sprite = w.normal_sprite;
            w.item_text_centered = false;
            w.scroll_track_sprite = POPUP_TRACK;
            w.scroll_thumb_top_sprite = POPUP_THUMB_TOP;
            w.scroll_thumb_sprite = POPUP_THUMB;
            w.scroll_thumb_bottom_sprite = POPUP_THUMB_BOTTOM;
            w.scroll_up_sprite = POPUP_UP;
            w.scroll_down_sprite = POPUP_DOWN;
        }
        let button =
            |ui: &mut UiRoot, id| ui.find_descendant(root, id).filter(|b| bind_click(ui, *b));
        let previous = button(ui, PREVIOUS_BUTTON_ID);
        let next = button(ui, NEXT_BUTTON_ID);
        let close = button(ui, CLOSE_BUTTON_ID);
        if page_field
            && let Some(bar) = ui.find_descendant(root, PAGE_SCROLLBAR_ID)
            && let Some(s) = ui.widget_mut::<Scrollbar>(bar)
        {
            s.model = ScrollSource::Element(page_text);
        }
        let mut c = Self {
            root,
            title,
            page_text,
            page_field,
            page_number,
            page_menu,
            previous,
            next,
            close,
            view: BookView {
                current_page: -1,
                ..BookView::default()
            },
            player: 0,
            shows_account: false,
            rendered_revision: None,
            was_open: false,
            close_sent: None,
        };
        c.refresh(ui, "");
        Some(c)
    }

    /// The slot's root.
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    /// What the page text built as ("typeable field", "read-only text").
    #[must_use]
    pub fn page_text_kind(&self) -> &'static str {
        if self.page_field {
            "typeable field"
        } else {
            "read-only text"
        }
    }

    /// The book as last shown.
    #[must_use]
    pub fn view(&self) -> &BookView {
        &self.view
    }

    /// Show `view`; returns `Some(open)` when the reader should show
    /// (`true`, a book opened) or hide (`false`, the book closed).
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &BookView,
        context: &BookContext<'_>,
    ) -> Option<bool> {
        self.player = context.player;
        self.shows_account = context.shows_account;
        if self.rendered_revision == Some(view.revision) {
            return None;
        }
        self.view.clone_from(view);
        if view.book_guid == 0 {
            self.close_sent = None;
        }
        self.refresh(ui, context.book_name);
        let open = view.book_guid != 0;
        if open == self.was_open {
            return None;
        }
        self.was_open = open;
        Some(open)
    }

    /// Show the book as last seen.
    pub fn refresh(&mut self, ui: &mut UiRoot, book_name: &str) {
        self.rendered_revision = Some(self.view.revision);
        let v = &self.view;
        let open = v.book_guid != 0;
        // An inscribed book is titled by its inscription; anything else
        // wears the item's own name.
        let title = if !open {
            String::new()
        } else if v.scribe_id != 0 {
            v.inscription.clone()
        } else {
            book_name.to_owned()
        };
        set_text(ui, self.title, &title);
        let page = self.page(v.current_page);
        let text = page
            .filter(|p| p.text_included != 0)
            .map_or_else(String::new, |p| p.page_text.clone());
        let editable_now =
            open && page.is_some_and(|p| p.text_included != 0 && editable(p, self.player));
        let max_chars = usize::try_from(v.max_num_chars_per_page).unwrap_or(0);
        let page_text = self.page_text;
        if self.page_field {
            ui.with::<Field, _>(page_text, |f, ui| {
                f.set_text(&text);
                f.set_editable(ui, page_text, editable_now);
                if max_chars > 0 {
                    f.max_characters = max_chars;
                }
            });
            let (w, h) = ui
                .element(page_text)
                .map_or((0.0, 0.0), |e| (e.width, e.height));
            if let Some(f) = ui.widget_mut::<Field>(page_text) {
                f.refresh_scroll_extents(w, h);
            }
        } else {
            set_text(ui, Some(page_text), &text);
        }
        let label = self.selected_label();
        set_text(ui, self.page_number, &label);
        let (current, max) = (v.current_page, v.max_num_pages);
        if let Some(b) = self.previous {
            ui.set_enabled(b, open && current > 0);
        }
        if let Some(b) = self.next {
            ui.set_enabled(b, open && current < max - 1);
        }
        self.refresh_menu(ui, &label);
    }

    fn page(&self, index: i32) -> Option<&BookPage> {
        usize::try_from(index)
            .ok()
            .and_then(|i| self.view.pages.get(i))
    }

    fn label(&self, index: i32) -> String {
        let page = self.page(index);
        page_label(
            index,
            page.map(|p| p.author_name.as_str()),
            page.map(|p| p.author_account.as_str()),
            self.shows_account,
        )
    }

    /// The entry the reader is on, as the list spells it ("" with no book).
    #[must_use]
    pub fn selected_label(&self) -> String {
        if self.view.book_guid == 0 || self.view.current_page < 0 {
            return String::new();
        }
        self.label(self.view.current_page)
    }

    /// The menu lists every page the book can hold, so a writer can turn
    /// to the next blank one.
    fn refresh_menu(&self, ui: &mut UiRoot, face: &str) {
        let Some(m) = self.page_menu else {
            return;
        };
        let v = &self.view;
        let items: Vec<MenuItem> = if v.book_guid == 0 || v.max_num_pages <= 0 {
            Vec::new()
        } else {
            (0..v.max_num_pages)
                .map(|i| MenuItem::new(self.label(i), u64::try_from(i).unwrap_or(0)))
                .collect()
        };
        let selected = (v.book_guid != 0
            && v.current_page >= 0
            && v.current_page < i32::try_from(items.len()).unwrap_or(i32::MAX))
        .then(|| u64::try_from(v.current_page).unwrap_or(0));
        let width = ui.element(m).map_or(0.0, |e| e.width);
        if let Some(w) = ui.widget_mut::<Menu>(m) {
            w.items = items;
            w.size_popup_to_width(width);
            w.selected = selected;
            w.button_label = face.to_owned();
        }
    }

    /// What the reader typed, or `None` when the page is not theirs to
    /// write (nothing to save).
    fn typed(&self, ui: &UiRoot) -> Option<String> {
        if !self.page_field {
            return None;
        }
        ui.widget::<Field>(self.page_text)
            .filter(|f| f.editable())
            .map(Field::text)
    }

    fn turn(&self, ui: &mut UiRoot, page: i32) {
        if self.view.book_guid == 0 {
            return;
        }
        let typed = self.typed(ui);
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Book(
                BookCommand::TurnWriting { page, typed },
            ))),
        );
    }

    /// The reader went away (hidden or closed): the open page is saved and
    /// the book closed, once.
    pub fn on_hidden(&mut self, ui: &mut UiRoot) {
        let book = self.view.book_guid;
        if book == 0 || self.close_sent == Some(book) {
            return;
        }
        self.close_sent = Some(book);
        self.was_open = false;
        let typed = self.typed(ui);
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Book(
                BookCommand::CloseWriting(typed),
            ))),
        );
    }
}

impl PanelController for BookPanelController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if Some(element) == self.previous {
            if clicked {
                self.turn(ui, self.view.current_page - 1);
            }
            return true;
        }
        if Some(element) == self.next {
            if clicked {
                self.turn(ui, self.view.current_page + 1);
            }
            return true;
        }
        if Some(element) == self.close {
            if clicked {
                self.on_hidden(ui);
                emit(ui, PanelOutput::ClosePanel(panel_id::BOOK));
            }
            return true;
        }
        if Some(element) == self.page_menu
            && let ElementMessage::MenuSelected(payload) = message
        {
            self.turn(ui, i32::try_from(*payload).unwrap_or(-1));
            return true;
        }
        false
    }
}
