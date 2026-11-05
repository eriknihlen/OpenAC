//! The character window's titles page (0x10000539): the displayed title,
//! the earned titles in alphabetical order, and the button that asks to
//! display the selected one.
//!
//! The titles are the session's ([`TitleFacts`]); a choice is a request and
//! changes nothing here until the server answers.

use ac_runtime::gameplay::GameplayCommand;
use ac_runtime::session::SessionCommand;
use ac_runtime::session::interface::TitleFacts;

use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, RowTemplates, link_list_scrollbar};
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{ListBox, Text};

/// The displayed title's text.
pub const CURRENT_DISPLAY_TITLE_TEXT_ID: u32 = 0x1000_052F;
/// The earned titles.
pub const TITLE_LIST_BOX_ID: u32 = 0x1000_0532;
/// Display the selected title.
pub const SET_DISPLAY_BUTTON_ID: u32 = 0x1000_0535;
/// A row's text.
pub const ROW_TEXT_ID: u32 = 0x1000_0537;
/// A row's height, pixels.
pub const ROW_HEIGHT: i32 = 24;
/// What an unknown or absent display title reads.
pub const UNKNOWN_TITLE_TEXT: &str = "Unknown";

/// The titles page's controller.
#[derive(Debug)]
pub struct CharacterTitlesController {
    list: ElementId,
    display_text: Option<ElementId>,
    set_display: Option<ElementId>,
    rows: Vec<(ElementId, u32)>,
    selected: Option<u32>,
    shown: Option<TitleFacts>,
}

/// The rows' order: by the resolved text (ordinal), then the id; titles
/// that are 0 or do not resolve have no row.
#[must_use]
pub fn sorted_titles(ids: &[u32], resolve: &dyn Fn(u32) -> Option<String>) -> Vec<(u32, String)> {
    let mut titles: Vec<(u32, String)> = ids
        .iter()
        .filter(|&&id| id != 0)
        .filter_map(|&id| resolve(id).map(|t| (id, t)))
        .collect();
    titles.sort_by(|a, b| a.1.cmp(&b.1).then(a.0.cmp(&b.0)));
    titles
}

impl CharacterTitlesController {
    /// Bind to the titles page (`None` without its list).
    pub fn bind(ui: &mut UiRoot, page: ElementId) -> Option<Self> {
        let list = ui
            .find_descendant(page, TITLE_LIST_BOX_ID)
            .filter(|l| ui.widget::<ListBox>(*l).is_some())?;
        ui.with::<ListBox, _>(list, |b, ui| b.set_line_height(ui, ROW_HEIGHT));
        link_list_scrollbar(ui, page, list);
        let display_text = ui
            .find_descendant(page, CURRENT_DISPLAY_TITLE_TEXT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some());
        let set_display = ui
            .find_descendant(page, SET_DISPLAY_BUTTON_ID)
            .filter(|b| bind_click(ui, *b));
        let c = Self {
            list,
            display_text,
            set_display,
            rows: Vec::new(),
            selected: None,
            shown: None,
        };
        c.refresh_button(ui);
        Some(c)
    }

    /// The list.
    #[must_use]
    pub fn list(&self) -> ElementId {
        self.list
    }

    /// The rows and their titles, in order.
    #[must_use]
    pub fn rows(&self) -> &[(ElementId, u32)] {
        &self.rows
    }

    /// The selected title.
    #[must_use]
    pub fn selected(&self) -> Option<u32> {
        self.selected
    }

    /// Show `view` when it changed: a new table or another displayed title
    /// drops the selection; an earned title keeps it.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &TitleFacts,
        resolve: &dyn Fn(u32) -> Option<String>,
        rows: &mut dyn RowTemplates,
    ) {
        if self.shown.as_ref() == Some(view) {
            return;
        }
        let previous = self.shown.replace(view.clone());
        let new_table = previous
            .as_ref()
            .is_none_or(|p| p.table_generation != view.table_generation);
        let display_changed = previous
            .as_ref()
            .is_none_or(|p| p.display_title_id != view.display_title_id);
        if new_table || display_changed {
            self.selected = None;
        }
        let set_changed = previous
            .as_ref()
            .is_none_or(|p| p.title_ids != view.title_ids);
        if new_table || set_changed {
            self.rebuild(ui, view, resolve, rows);
        }
        self.apply_highlights(ui);
        let text = resolve(view.display_title_id).unwrap_or_else(|| UNKNOWN_TITLE_TEXT.to_owned());
        if let Some(t) = self.display_text.and_then(|t| ui.widget_mut::<Text>(t)) {
            let color = t.default_color;
            t.set_lines(vec![Line::new(text, color)]);
        }
        self.refresh_button(ui);
    }

    fn rebuild(
        &mut self,
        ui: &mut UiRoot,
        view: &TitleFacts,
        resolve: &dyn Fn(u32) -> Option<String>,
        rows: &mut dyn RowTemplates,
    ) {
        let list = self.list;
        let template = ui
            .widget::<ListBox>(list)
            .and_then(|b| b.templates.first().copied());
        ui.with::<ListBox, _>(list, |b, ui| b.flush_preserving_scroll(ui));
        self.rows.clear();
        let Some(t) = template else {
            return;
        };
        for (id, text) in sorted_titles(&view.title_ids, resolve) {
            let Some(row) = rows.build_row(ui, t.layout, t.element) else {
                continue;
            };
            ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, row));
            bind_click(ui, row);
            if let Some(w) = ui
                .find_descendant(row, ROW_TEXT_ID)
                .and_then(|e| ui.widget_mut::<Text>(e))
            {
                let color = w.default_color;
                w.set_lines(vec![Line::new(text, color)]);
            }
            self.rows.push((row, id));
        }
        if self
            .selected
            .is_some_and(|s| !self.rows.iter().any(|(_, id)| *id == s))
        {
            self.selected = None;
        }
    }

    fn apply_highlights(&self, ui: &mut UiRoot) {
        for &(row, id) in &self.rows {
            let state = if Some(id) == self.selected {
                states::HIGHLIGHT
            } else {
                states::NORMAL
            };
            ui.set_retail_state(row, state);
        }
    }

    fn display_title(&self) -> u32 {
        self.shown.as_ref().map_or(0, |v| v.display_title_id)
    }

    fn refresh_button(&self, ui: &mut UiRoot) {
        let Some(b) = self.set_display else {
            return;
        };
        let ghosted = self.selected.is_none_or(|s| s == self.display_title());
        ui.set_retail_state(
            b,
            if ghosted {
                states::GHOSTED
            } else {
                states::NORMAL
            },
        );
        ui.set_enabled(b, !ghosted);
    }

    /// Select title `id`'s row.
    pub fn select(&mut self, ui: &mut UiRoot, id: u32) {
        self.selected = Some(id);
        self.apply_highlights(ui);
        self.refresh_button(ui);
    }

    /// A message for the titles page; true when it was the page's.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if Some(element) == self.set_display {
            if clicked
                && let Some(id) = self.selected
                && id != self.display_title()
            {
                emit(
                    ui,
                    PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::SetTitle(id))),
                );
            }
            return true;
        }
        let Some(&(_, id)) = self.rows.iter().find(|(r, _)| *r == element) else {
            return false;
        };
        if clicked {
            self.select(ui, id);
        }
        true
    }
}

/// The title enumeration's names.
pub const TITLE_ENUM_MAPPER_ID: u32 = 0x2200_0041;
/// The string table the names key into.
pub const TITLE_STRING_TABLE_ID: u32 = 0x2300_000E;

/// A title's text: its enumeration name looked up in the titles string
/// table, cached per id.
#[derive(Debug, Default)]
pub struct TitleNames {
    mapper: Option<ac_dat::objects::EnumMapper>,
    cache: std::collections::HashMap<u32, Option<String>>,
}

impl TitleNames {
    /// The names of `dats`.
    #[must_use]
    pub fn new(dats: &ac_dat::DatCollection) -> Self {
        Self {
            mapper: dats.get(TITLE_ENUM_MAPPER_ID).ok().flatten(),
            cache: std::collections::HashMap::new(),
        }
    }

    /// Title `id`'s text (`None` for 0 or an id with no name).
    pub fn resolve(&mut self, strings: &crate::strings::StringResolver, id: u32) -> Option<String> {
        if id == 0 {
            return None;
        }
        if let Some(cached) = self.cache.get(&id) {
            return cached.clone();
        }
        let resolved = self
            .mapper
            .as_ref()
            .and_then(|m| m.get(id))
            .filter(|name| !name.is_empty())
            .and_then(|name| {
                strings.resolve(TITLE_STRING_TABLE_ID, crate::strings::compute_hash(name), 0)
            });
        self.cache.insert(id, resolved.clone());
        resolved
    }
}
