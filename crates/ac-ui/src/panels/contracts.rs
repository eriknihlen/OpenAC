//! The journal's contracts page: a list of tracked contracts (name and
//! progress) over a detail pane (status, contact, the two locations, the
//! description and the timer), and the abandon button.
//!
//! The page shows the tracker handed to [`ContractsPageController::update`]
//! each frame: the list is rebuilt when the tracker's revision moves, the
//! detail pane follows the clock every update. Abandoning sends the request
//! and removes nothing: the row goes when the server's delete arrives.

use ac_core::quests::{ContractCatalog, build_progress_text};
use ac_core::text::format_duration;
use ac_runtime::communication::TrackedContract;
use ac_runtime::session::SessionCommand;

use crate::draw::Color;
use crate::host::PanelController;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, RowTemplates, link_list_scrollbar};
use crate::radar::RadarCoordinates;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Button, ListBox, Text};

/// The page in the journal panel.
pub const PAGE_ID: u32 = 0x1000_05D4;
/// The layout of the row template (the list's template entry).
pub const ROW_TEMPLATE_LAYOUT_ID: u32 = 0x2100_0069;
/// The row template element.
pub const ROW_TEMPLATE_ELEMENT_ID: u32 = 0x1000_05D7;
/// The contract list.
pub const LIST_ID: u32 = 0x1000_05CF;
/// A row's name.
pub const ROW_NAME_ID: u32 = 0x1000_05D1;
/// A row's progress.
pub const ROW_STATUS_ID: u32 = 0x1000_05D2;
/// The detail pane's status.
pub const STATUS_VALUE_ID: u32 = 0x1000_05DF;
/// The detail pane's contact.
pub const CONTACT_VALUE_ID: u32 = 0x1000_05E0;
/// The contact's location.
pub const CONTACT_LOCATION_VALUE_ID: u32 = 0x1000_05E1;
/// The quest area's location.
pub const QUEST_LOCATION_VALUE_ID: u32 = 0x1000_05E2;
/// The description.
pub const DESCRIPTION_ID: u32 = 0x1000_05DE;
/// The timer ("Timed:").
pub const TIMED_VALUE_ID: u32 = 0x1000_05E3;
/// The abandon button.
pub const ABANDON_BUTTON_ID: u32 = 0x1000_05DC;

/// The selected row's name colour.
pub const SELECTED_NAME_COLOR: Color = Color::WHITE;

/// What the page reads, borrowed for one update.
#[derive(Debug, Clone, Copy)]
pub struct ContractsView<'a> {
    /// Every tracked contract, by id.
    pub contracts: &'a [TrackedContract],
    /// The contract the server put on display (0: none).
    pub display_contract_id: u32,
    /// The tracker's revision (moves on every change).
    pub revision: u64,
    /// The authored contract texts.
    pub catalog: &'a ContractCatalog,
    /// Now, UTC milliseconds since the Unix epoch (the clock the trackers'
    /// arrival times are on).
    pub now_unix_ms: i64,
}

impl ContractsView<'_> {
    fn get(&self, id: u32) -> Option<&TrackedContract> {
        self.contracts.iter().find(|c| c.tracker.contract_id == id)
    }
}

#[derive(Debug, Clone)]
struct Row {
    contract_id: u32,
    element: ElementId,
    name: Option<ElementId>,
    name_text: String,
    unselected: Color,
}

/// The contracts page's controller.
#[derive(Debug)]
pub struct ContractsPageController {
    list: Option<ElementId>,
    status: Option<ElementId>,
    contact: Option<ElementId>,
    contact_location: Option<ElementId>,
    quest_location: Option<ElementId>,
    description: Option<ElementId>,
    timed: Option<ElementId>,
    abandon: Option<ElementId>,
    rows: Vec<Row>,
    row_ids: Vec<u32>,
    rendered_revision: Option<u64>,
    selected: u32,
}

#[allow(clippy::cast_precision_loss)]
fn seconds(ms: i64) -> f64 {
    ms as f64 / 1000.0
}

/// A cell's map coordinates, "Indoors" when it has none, "" when unset.
#[must_use]
pub fn location_text(cell_id: u32) -> String {
    if cell_id == 0 {
        return String::new();
    }
    RadarCoordinates::from_cell(cell_id).map_or_else(|| "Indoors".to_owned(), |c| c.combined_text())
}

fn text_color(ui: &UiRoot, id: ElementId) -> Color {
    ui.widget::<Text>(id)
        .map_or(Color::WHITE, |t| t.default_color)
}

fn set_text(ui: &mut UiRoot, id: Option<ElementId>, value: &str, color: Option<Color>) {
    let Some(id) = id else {
        return;
    };
    let color = color.unwrap_or_else(|| text_color(ui, id));
    if let Some(t) = ui.widget_mut::<Text>(id) {
        let lines = vec![Line::new(value, color)];
        if t.content_lines() != lines {
            t.set_lines(lines);
        }
    }
}

impl ContractsPageController {
    /// Bind to the contracts page `page` (its list's scrollbar is linked).
    pub fn bind(ui: &mut UiRoot, page: ElementId) -> Self {
        let find = |ui: &UiRoot, id| ui.find_descendant(page, id);
        let text = |ui: &UiRoot, id| find(ui, id).filter(|t| ui.widget::<Text>(*t).is_some());
        let list = find(ui, LIST_ID).filter(|l| ui.widget::<ListBox>(*l).is_some());
        if let Some(l) = list {
            link_list_scrollbar(ui, page, l);
        }
        let abandon = find(ui, ABANDON_BUTTON_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some() && bind_click(ui, *b));
        Self {
            list,
            status: text(ui, STATUS_VALUE_ID),
            contact: text(ui, CONTACT_VALUE_ID),
            contact_location: text(ui, CONTACT_LOCATION_VALUE_ID),
            quest_location: text(ui, QUEST_LOCATION_VALUE_ID),
            description: text(ui, DESCRIPTION_ID),
            timed: text(ui, TIMED_VALUE_ID),
            abandon,
            rows: Vec::new(),
            row_ids: Vec::new(),
            rendered_revision: None,
            selected: 0,
        }
    }

    /// The contract the detail pane shows (0: none).
    #[must_use]
    pub fn selected_contract_id(&self) -> u32 {
        self.selected
    }

    /// The contract ids in list order.
    #[must_use]
    pub fn row_contract_ids(&self) -> &[u32] {
        &self.row_ids
    }

    /// The row elements in list order.
    #[must_use]
    pub fn row_elements(&self) -> Vec<ElementId> {
        self.rows.iter().map(|r| r.element).collect()
    }

    /// Rebuild the list when the tracker moved; refresh the detail pane.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &ContractsView<'_>,
        rows: &mut dyn RowTemplates,
    ) {
        if self.rendered_revision != Some(view.revision) {
            self.refresh(ui, view, rows);
        }
        self.refresh_detail(ui, view);
    }

    fn refresh(
        &mut self,
        ui: &mut UiRoot,
        view: &ContractsView<'_>,
        templates: &mut dyn RowTemplates,
    ) {
        self.rendered_revision = Some(view.revision);
        let mut contracts: Vec<&TrackedContract> = view.contracts.iter().collect();
        contracts.sort_by_key(|c| c.tracker.contract_id);
        // The server nominates the contract on display; otherwise the
        // first row stands.
        if view.display_contract_id != 0 {
            self.selected = view.display_contract_id;
        }
        if self.selected == 0
            && let Some(first) = contracts.first()
        {
            self.selected = first.tracker.contract_id;
        }
        if contracts.is_empty() {
            self.selected = 0;
        }
        self.row_ids.clear();
        self.rows.clear();
        let template = self.list.and_then(|l| {
            ui.widget::<ListBox>(l)
                .and_then(|b| b.templates.first().copied())
        });
        if let Some(l) = self.list {
            ui.with::<ListBox, _>(l, |b, ui| b.flush_preserving_scroll(ui));
        }
        let now = seconds(view.now_unix_ms);
        for c in contracts {
            let id = c.tracker.contract_id;
            self.row_ids.push(id);
            let (Some(list), Some(t)) = (self.list, template) else {
                continue;
            };
            let Some(row) = templates.build_row(ui, t.layout, t.element) else {
                continue;
            };
            ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, row));
            let entry = view.catalog.lookup(id);
            let name = ui
                .find_descendant(row, ROW_NAME_ID)
                .filter(|t| ui.widget::<Text>(*t).is_some());
            set_text(ui, name, &entry.contract_name, None);
            if let Some(status) = ui
                .find_descendant(row, ROW_STATUS_ID)
                .filter(|t| ui.widget::<Text>(*t).is_some())
            {
                let progress = build_progress_text(
                    c.tracker.stage,
                    c.tracker.time_when_repeats,
                    seconds(c.received_unix_ms),
                    entry,
                    now,
                );
                set_text(ui, Some(status), &progress, None);
            }
            // A generic container row is click-through until it opts in.
            bind_click(ui, row);
            self.rows.push(Row {
                contract_id: id,
                element: row,
                name,
                name_text: entry.contract_name.clone(),
                unselected: name.map_or(Color::WHITE, |n| text_color(ui, n)),
            });
        }
        self.apply_selection_highlight(ui);
    }

    fn apply_selection_highlight(&self, ui: &mut UiRoot) {
        for r in &self.rows {
            let color = if r.contract_id == self.selected {
                SELECTED_NAME_COLOR
            } else {
                r.unselected
            };
            set_text(ui, r.name, &r.name_text, Some(color));
        }
    }

    /// Show contract `contract_id` in the detail pane (its row
    /// highlighted); the pane fills at the next update.
    pub fn select(&mut self, ui: &mut UiRoot, contract_id: u32) {
        self.selected = contract_id;
        self.apply_selection_highlight(ui);
    }

    /// Ask the server to abandon the selected contract.
    pub fn abandon_selected(&self, ui: &mut UiRoot) {
        if self.selected == 0 {
            return;
        }
        emit(
            ui,
            PanelOutput::Session(SessionCommand::AbandonContract(self.selected)),
        );
    }

    fn refresh_detail(&self, ui: &mut UiRoot, view: &ContractsView<'_>) {
        let tracked = (self.selected != 0)
            .then(|| view.get(self.selected))
            .flatten();
        let Some(c) = tracked else {
            for t in [
                self.status,
                self.contact,
                self.contact_location,
                self.quest_location,
                self.description,
                self.timed,
            ] {
                set_text(ui, t, "", None);
            }
            return;
        };
        let entry = view.catalog.lookup(self.selected);
        let now = seconds(view.now_unix_ms);
        let received = seconds(c.received_unix_ms);
        let status = build_progress_text(
            c.tracker.stage,
            c.tracker.time_when_repeats,
            received,
            entry,
            now,
        );
        set_text(ui, self.status, &status, None);
        set_text(ui, self.contact, &entry.name_npc_start, None);
        set_text(
            ui,
            self.contact_location,
            &location_text(entry.location_npc_start_cell),
            None,
        );
        set_text(
            ui,
            self.quest_location,
            &location_text(entry.location_quest_area_cell),
            None,
        );
        set_text(ui, self.description, &entry.description, None);
        // "Timed:" is the other wire timer, the one the status never reads.
        let timed = if c.tracker.time_when_done > 0.0 {
            format_duration((c.tracker.time_when_done - (now - received)).max(0.0))
        } else {
            String::new()
        };
        set_text(ui, self.timed, &timed, None);
    }

    fn row_of(&self, ui: &UiRoot, element: ElementId) -> Option<u32> {
        let mut cur = Some(element);
        while let Some(e) = cur {
            if let Some(r) = self.rows.iter().find(|r| r.element == e) {
                return Some(r.contract_id);
            }
            if Some(e) == self.list {
                return None;
            }
            cur = ui.parent(e);
        }
        None
    }
}

impl PanelController for ContractsPageController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let clicked = matches!(message, ElementMessage::Clicked { .. });
        if Some(element) == self.abandon {
            if clicked {
                self.abandon_selected(ui);
            }
            return true;
        }
        if let Some(id) = self.row_of(ui, element) {
            if clicked {
                self.select(ui, id);
            }
            return true;
        }
        false
    }
}
