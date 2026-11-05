//! The journal panel (layout 0x2100006E, slot 0x10000559): a tab panel
//! with the contracts page, the notes page and the page list, and a close
//! button. Leaving the notes tab (or hiding the panel) commits what was
//! typed; a page opened from the list shows on the notes tab.

use ac_runtime::communication::{JournalCommand, JournalView};
use ac_runtime::session::SessionCommand;

use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::panels::contracts::{self, ContractsPageController, ContractsView};
use crate::panels::items::{bind_click, emit};
use crate::panels::journal_notes::{self, JournalNotesPageController};
use crate::panels::journal_pages::{self, JournalPageListController};
use crate::panels::{PanelOutput, RowTemplates, panel_id};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;
use crate::widgets::{Button, TabPanel};

/// The layout hosting the main panels.
pub const HOST_LAYOUT_ID: u32 = 0x2100_006E;
/// The journal panel's slot in it.
pub const SLOT_ELEMENT_ID: u32 = 0x1000_0559;
/// The notes page.
pub const NOTES_PAGE_ID: u32 = journal_notes::PAGE_ID;
/// The page list.
pub const PAGE_LIST_PAGE_ID: u32 = journal_pages::PAGE_ID;
/// The close button.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_0562;

/// The journal panel's controller.
#[derive(Debug)]
pub struct JournalPanelController {
    tab_panel: ElementId,
    close: Option<ElementId>,
    contracts: Option<ContractsPageController>,
    notes: Option<JournalNotesPageController>,
    page_list: Option<JournalPageListController>,
}

impl JournalPanelController {
    /// Bind to the imported journal panel (`None` unless its root is a tab
    /// panel).
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Option<Self> {
        let tab_panel = layout.root;
        ui.widget::<TabPanel>(tab_panel)?;
        let close = ui
            .find_descendant(tab_panel, CLOSE_BUTTON_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some() && bind_click(ui, *b));
        let contracts = ui
            .find_descendant(tab_panel, contracts::PAGE_ID)
            .map(|page| ContractsPageController::bind(ui, page));
        let notes = ui
            .find_descendant(tab_panel, NOTES_PAGE_ID)
            .map(|page| JournalNotesPageController::bind(ui, page));
        let page_list = ui
            .find_descendant(tab_panel, PAGE_LIST_PAGE_ID)
            .map(|page| JournalPageListController::bind(ui, page));
        Some(Self {
            tab_panel,
            close,
            contracts,
            notes,
            page_list,
        })
    }

    /// The tab panel.
    #[must_use]
    pub fn tab_panel(&self) -> ElementId {
        self.tab_panel
    }

    /// The contracts page.
    #[must_use]
    pub fn contracts(&self) -> Option<&ContractsPageController> {
        self.contracts.as_ref()
    }

    /// The notes page.
    #[must_use]
    pub fn notes(&self) -> Option<&JournalNotesPageController> {
        self.notes.as_ref()
    }

    /// The page list.
    #[must_use]
    pub fn page_list(&self) -> Option<&JournalPageListController> {
        self.page_list.as_ref()
    }

    /// Start the tabs: the authored default page shows.
    pub fn activate_tabs(&mut self, ui: &mut UiRoot) {
        let t = self.tab_panel;
        ui.with::<TabPanel, _>(t, |w, ui| w.activate(ui, t));
    }

    fn switch_to(&self, ui: &mut UiRoot, page: u32) {
        let t = self.tab_panel;
        ui.with::<TabPanel, _>(t, |w, ui| w.switch_to(ui, t, page));
    }

    fn showing(&self, ui: &UiRoot, page: u32) -> bool {
        ui.widget::<TabPanel>(self.tab_panel)
            .is_some_and(|t| t.active_page() == page)
    }

    /// Show the contracts page.
    pub fn show_contracts(&mut self, ui: &mut UiRoot) {
        self.switch_to(ui, contracts::PAGE_ID);
    }

    /// Show the notes page.
    pub fn show_notes(&mut self, ui: &mut UiRoot) {
        self.switch_to(ui, NOTES_PAGE_ID);
    }

    /// Show the page list.
    pub fn show_page_list(&mut self, ui: &mut UiRoot) {
        self.switch_to(ui, PAGE_LIST_PAGE_ID);
    }

    /// The contracts page is the active tab.
    #[must_use]
    pub fn is_showing_contracts(&self, ui: &UiRoot) -> bool {
        self.showing(ui, contracts::PAGE_ID)
    }

    /// The notes page is the active tab.
    #[must_use]
    pub fn is_showing_notes(&self, ui: &UiRoot) -> bool {
        self.showing(ui, NOTES_PAGE_ID)
    }

    /// The page list is the active tab.
    #[must_use]
    pub fn is_showing_page_list(&self, ui: &UiRoot) -> bool {
        self.showing(ui, PAGE_LIST_PAGE_ID)
    }

    /// Show `view` on the contracts page.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &ContractsView<'_>,
        rows: &mut dyn RowTemplates,
    ) {
        if let Some(c) = self.contracts.as_mut() {
            c.update(ui, view, rows);
        }
    }

    /// Show the journal on the notes page and the page list (`player_cell`
    /// is what a recorded location reads, `now_unix_ms` the countdown's
    /// clock).
    pub fn update_journal(
        &mut self,
        ui: &mut UiRoot,
        journal: &JournalView,
        player_cell: u32,
        now_unix_ms: i64,
        rows: &mut dyn RowTemplates,
    ) {
        if let Some(n) = self.notes.as_mut() {
            n.update(ui, journal, player_cell, now_unix_ms);
        }
        if let Some(p) = self.page_list.as_mut() {
            p.update(ui, journal, rows);
        }
    }

    /// The panel was hidden: what was typed on the notes page commits.
    pub fn on_hidden(&mut self, ui: &mut UiRoot) {
        if let Some(n) = &self.notes {
            n.commit(ui);
        }
    }

    /// Open journal page `page` on the notes tab (the page list's double
    /// click): what was typed commits first.
    pub fn open_page(&mut self, ui: &mut UiRoot, page: usize) {
        if let Some(n) = &self.notes {
            n.commit(ui);
        }
        emit(
            ui,
            PanelOutput::Session(SessionCommand::Journal(JournalCommand::GotoPage(page))),
        );
        self.show_notes(ui);
    }
}

impl PanelController for JournalPanelController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.close {
            if matches!(message, ElementMessage::Clicked { .. }) {
                emit(ui, PanelOutput::ClosePanel(panel_id::JOURNAL));
            }
            return true;
        }
        if element == self.tab_panel
            && let ElementMessage::TabChanged { previous, .. } = message
        {
            // Leaving the notes tab commits what was typed.
            if *previous == NOTES_PAGE_ID
                && let Some(n) = &self.notes
            {
                n.commit(ui);
            }
            return false;
        }
        if let Some(p) = self.page_list.as_mut()
            && let Some(open) = p.on_message(ui, element, message)
        {
            if let Some(page) = open {
                self.open_page(ui, page);
            }
            return true;
        }
        if let Some(n) = self.notes.as_mut()
            && n.on_element_message(ui, element, message)
        {
            return true;
        }
        self.contracts
            .as_mut()
            .is_some_and(|c| c.on_element_message(ui, element, message))
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `JournalPanelController`: the close button toggles the panel
    //! shut; the contracts page is the one the contracts key shows.

    use super::*;
    use crate::element_info::{ElementInfo, ImageMedia, TabEntry};
    use crate::import::ImportedLayout;
    use crate::root::UiOutput;
    use crate::tree::Element;
    use crate::widgets::Panel;
    use std::sync::Arc;

    #[test]
    fn the_close_button_closes_the_journal_and_the_contracts_tab_shows() {
        let mut ui = UiRoot::new();
        ui.set_size(800.0, 600.0);
        let root = ui.root();
        let tabs = vec![
            TabEntry {
                button: 0x1000_0001,
                page: NOTES_PAGE_ID,
                is_default: true,
            },
            TabEntry {
                button: 0x1000_0002,
                page: contracts::PAGE_ID,
                is_default: false,
            },
        ];
        let mut e = Element::at(0.0, 0.0, 300.0, 300.0);
        e.dat_element_id = SLOT_ELEMENT_ID;
        let info = Arc::new(ElementInfo {
            id: SLOT_ELEMENT_ID,
            tab_table: tabs,
            ..ElementInfo::default()
        });
        let panel = ui.add(root, e, TabPanel::new(info));
        let mut by_id = std::collections::HashMap::new();
        for id in [0x1000_0001, 0x1000_0002] {
            let info = Arc::new(ElementInfo {
                id,
                kind: 1,
                width: 20.0,
                height: 10.0,
                ..ElementInfo::default()
            });
            let mut e = Element::at(0.0, 0.0, 20.0, 10.0);
            e.dat_element_id = id;
            let b = ui.add(panel, e, Button::new(info, None, Vec::new(), 0.0));
            by_id.insert(id, b);
        }
        for id in [NOTES_PAGE_ID, contracts::PAGE_ID] {
            let mut e = Element::at(0.0, 20.0, 300.0, 280.0);
            e.dat_element_id = id;
            let p = ui.add(panel, e, Panel::default());
            by_id.insert(id, p);
        }
        let mut close_info = ElementInfo {
            id: CLOSE_BUTTON_ID,
            kind: 1,
            width: 16.0,
            height: 16.0,
            ..ElementInfo::default()
        };
        close_info.state_media.insert(
            "Normal".to_owned(),
            ImageMedia {
                file: 1,
                draw_mode: 1,
            },
        );
        let mut e = Element::at(280.0, 0.0, 16.0, 16.0);
        e.dat_element_id = CLOSE_BUTTON_ID;
        let close = ui.add(
            panel,
            e,
            Button::new(Arc::new(close_info), None, Vec::new(), 0.0),
        );
        by_id.insert(CLOSE_BUTTON_ID, close);
        let layout = ImportedLayout::from_parts(panel, by_id);
        let mut journal = JournalPanelController::bind(&mut ui, &layout).expect("a tab panel");
        assert!(journal.contracts().is_some());
        journal.activate_tabs(&mut ui);
        assert!(!journal.is_showing_contracts(&ui));
        journal.show_contracts(&mut ui);
        assert!(journal.is_showing_contracts(&ui));
        ui.drain_output();
        let clicked = ElementMessage::Clicked { x: 1, y: 1 };
        assert!(journal.on_element_message(&mut ui, close, &clicked));
        let out = ui.drain_output();
        assert!(
            matches!(
                out.as_slice(),
                [UiOutput::Panel(PanelOutput::ClosePanel(panel_id::JOURNAL))]
            ),
            "{out:?}"
        );
    }
}
