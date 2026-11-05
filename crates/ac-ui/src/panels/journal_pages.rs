//! The journal's page list (0x10000564): one row per page (number, title,
//! countdown, label) filtered by the search field, a selection, delete and
//! reset; two clicks on a row within a second open that page on the notes
//! tab.

use ac_core::journal::JournalPage;
use ac_core::text::format_duration;
use ac_runtime::communication::{JournalCommand, JournalView};
use ac_runtime::session::SessionCommand;

use crate::draw::Color;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, RowTemplates};
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Field, ListBox, Text};

/// The page in the journal panel.
pub const PAGE_ID: u32 = 0x1000_0564;
/// The row template's layout.
pub const ROW_TEMPLATE_LAYOUT_ID: u32 = 0x2100_0067;
/// The row template.
pub const ROW_TEMPLATE_ELEMENT_ID: u32 = 0x1000_0589;
/// The list.
pub const LIST_ID: u32 = 0x1000_0583;
/// Delete the selected page.
pub const DELETE_BUTTON_ID: u32 = 0x1000_0585;
/// The search field.
pub const SEARCH_FIELD_ID: u32 = 0x1000_0587;
/// Clear the search.
pub const RESET_BUTTON_ID: u32 = 0x1000_0588;
/// A row's page number.
pub const ROW_NUMBER_ID: u32 = 0x1000_058A;
/// A row's title.
pub const ROW_TITLE_ID: u32 = 0x1000_058B;
/// A row's countdown.
pub const ROW_TIMER_ID: u32 = 0x1000_058C;
/// A row's label.
pub const ROW_LABEL_ID: u32 = 0x1000_058D;

/// Two clicks on a row within this long open it, milliseconds.
pub const DOUBLE_CLICK_WINDOW_MS: i64 = 1000;

/// The selected row's title colour.
pub const SELECTED_TITLE_COLOR: Color = Color::WHITE;

/// Whether `page` holds `search` in its label, title or notes (case
/// matters, as the game's search does; an empty search matches all).
#[must_use]
pub fn page_contains(page: &JournalPage, search: &str) -> bool {
    search.is_empty()
        || page.label.contains(search)
        || page.title.contains(search)
        || page.notes.contains(search)
}

#[derive(Debug, Clone)]
struct Row {
    page: usize,
    element: ElementId,
    title: Option<ElementId>,
    title_text: String,
    unselected: Color,
}

/// The page list's controller.
#[derive(Debug)]
pub struct JournalPageListController {
    list: Option<ElementId>,
    search: Option<ElementId>,
    delete: Option<ElementId>,
    reset: Option<ElementId>,
    rows: Vec<Row>,
    row_pages: Vec<usize>,
    rendered_revision: Option<u64>,
    rendered_search: String,
    selected_page: usize,
    last_click: Option<(usize, i64)>,
}

fn set_text(ui: &mut UiRoot, id: Option<ElementId>, s: &str, color: Option<Color>) {
    if let Some(t) = id.and_then(|t| ui.widget_mut::<Text>(t)) {
        let color = color.unwrap_or(t.default_color);
        t.set_lines(vec![Line::new(s, color)]);
    }
}

impl JournalPageListController {
    /// Bind to the page list `page`.
    pub fn bind(ui: &mut UiRoot, page: ElementId) -> Self {
        let button =
            |ui: &mut UiRoot, id| ui.find_descendant(page, id).filter(|b| bind_click(ui, *b));
        Self {
            list: ui
                .find_descendant(page, LIST_ID)
                .filter(|l| ui.widget::<ListBox>(*l).is_some()),
            search: ui
                .find_descendant(page, SEARCH_FIELD_ID)
                .filter(|f| ui.widget::<Field>(*f).is_some()),
            delete: button(ui, DELETE_BUTTON_ID),
            reset: button(ui, RESET_BUTTON_ID),
            rows: Vec::new(),
            row_pages: Vec::new(),
            rendered_revision: None,
            rendered_search: String::new(),
            selected_page: 0,
            last_click: None,
        }
    }

    /// The pages listed (1-based journal page numbers), in order.
    #[must_use]
    pub fn row_pages(&self) -> &[usize] {
        &self.row_pages
    }

    /// The selected page (0: none).
    #[must_use]
    pub fn selected_page(&self) -> usize {
        self.selected_page
    }

    /// The row elements, in order.
    #[must_use]
    pub fn row_elements(&self) -> Vec<ElementId> {
        self.rows.iter().map(|r| r.element).collect()
    }

    fn search_text(&self, ui: &UiRoot) -> String {
        self.search
            .and_then(|s| ui.widget::<Field>(s))
            .map(Field::text)
            .unwrap_or_default()
    }

    /// Rebuild the list when the journal or the search changed.
    pub fn update(&mut self, ui: &mut UiRoot, view: &JournalView, rows: &mut dyn RowTemplates) {
        let search = self.search_text(ui);
        if self.rendered_revision != Some(view.revision) || search != self.rendered_search {
            self.refresh(ui, view, rows);
        }
    }

    /// Rebuild the list.
    pub fn refresh(&mut self, ui: &mut UiRoot, view: &JournalView, rows: &mut dyn RowTemplates) {
        self.rendered_revision = Some(view.revision);
        self.rendered_search = self.search_text(ui);
        self.rows.clear();
        self.row_pages.clear();
        let template = self.list.and_then(|l| {
            ui.widget::<ListBox>(l)
                .and_then(|b| b.templates.first().copied())
        });
        if let Some(l) = self.list {
            ui.with::<ListBox, _>(l, |b, ui| b.flush_preserving_scroll(ui));
        }
        for (i, page) in view.pages.iter().enumerate() {
            if !page_contains(page, &self.rendered_search) {
                continue;
            }
            let number = i + 1;
            self.row_pages.push(number);
            let (Some(list), Some(t)) = (self.list, template) else {
                continue;
            };
            let Some(row) = rows.build_row(ui, t.layout, t.element) else {
                continue;
            };
            ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, row));
            let text = |ui: &UiRoot, id| {
                ui.find_descendant(row, id)
                    .filter(|t| ui.widget::<Text>(*t).is_some())
            };
            let (n, title, timer, label) = (
                text(ui, ROW_NUMBER_ID),
                text(ui, ROW_TITLE_ID),
                text(ui, ROW_TIMER_ID),
                text(ui, ROW_LABEL_ID),
            );
            set_text(ui, n, &number.to_string(), None);
            set_text(ui, title, &page.title, None);
            let countdown = if page.is_timer_running() {
                format_duration(page.running_timer_seconds)
            } else if page.has_timer() {
                #[allow(clippy::cast_precision_loss)]
                format_duration(page.timer_duration_seconds() as f64)
            } else {
                String::new()
            };
            set_text(ui, timer, &countdown, None);
            set_text(ui, label, &page.label, None);
            bind_click(ui, row);
            let unselected = title
                .and_then(|t| ui.widget::<Text>(t))
                .map_or(Color::WHITE, |t| t.default_color);
            self.rows.push(Row {
                page: number,
                element: row,
                title,
                title_text: page.title.clone(),
                unselected,
            });
        }
        if self.selected_page != 0 && !self.row_pages.contains(&self.selected_page) {
            self.selected_page = 0;
        }
        self.apply_selection(ui);
    }

    fn apply_selection(&self, ui: &mut UiRoot) {
        for r in &self.rows {
            let color = if r.page == self.selected_page {
                SELECTED_TITLE_COLOR
            } else {
                r.unselected
            };
            set_text(ui, r.title, &r.title_text, Some(color));
        }
    }

    /// Select `page`.
    pub fn select(&mut self, ui: &mut UiRoot, page: usize) {
        self.selected_page = page;
        self.apply_selection(ui);
    }

    /// A click on the row of `page` at `now_ms`: the second click on the
    /// same row within a second asks to open it (returned), else it
    /// selects.
    pub fn click(&mut self, ui: &mut UiRoot, page: usize, now_ms: i64) -> Option<usize> {
        if let Some((last, at)) = self.last_click
            && last == page
            && now_ms - at <= DOUBLE_CLICK_WINDOW_MS
        {
            self.last_click = None;
            self.select(ui, page);
            return Some(page);
        }
        self.last_click = Some((page, now_ms));
        self.select(ui, page);
        None
    }

    fn delete_selected(&mut self, ui: &mut UiRoot) {
        if self.selected_page == 0 {
            return;
        }
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Journal(JournalCommand::DeletePage(
                self.selected_page,
            ))),
        );
        self.selected_page = 0;
        self.apply_selection(ui);
    }

    /// A message for the page list: `Some(page)` when a row asks to be
    /// opened, the outer `Some` when the message was the list's.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> Option<Option<usize>> {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if Some(element) == self.delete {
            if clicked {
                self.delete_selected(ui);
            }
            return Some(None);
        }
        if Some(element) == self.reset {
            if clicked && let Some(f) = self.search.and_then(|s| ui.widget_mut::<Field>(s)) {
                f.set_text("");
            }
            return Some(None);
        }
        let page = self.rows.iter().find(|r| r.element == element)?.page;
        if !clicked {
            return Some(None);
        }
        let now = ui.now_ms();
        Some(self.click(ui, page, now))
    }
}
