//! The tab panel: the authored tab table (dat property 0x2E) pairs tab
//! buttons with pages; one page shows at a time and its button is open.
//! A click on a tab button bubbles up to the panel, which switches pages
//! and reports [`ElementMessage::TabChanged`].

use std::sync::Arc;

use crate::draw::RenderContext;
use crate::element_info::{ElementInfo, TabEntry};
use crate::event::{UiEvent, kind};
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::tree::{ElementId, Widget};
use crate::widgets::{Button, DatElement};

/// A tab panel.
#[derive(Debug, Clone)]
pub struct TabPanel {
    /// The element's dat look and states.
    pub inner: DatElement,
    tabs: Vec<TabEntry>,
    unresolved: Vec<TabEntry>,
    behavior_active: bool,
    active_page: u32,
}

impl TabPanel {
    /// A tab panel over `info`, with its authored tab table.
    #[must_use]
    pub fn new(info: Arc<ElementInfo>) -> Self {
        Self {
            tabs: info.tab_table.clone(),
            inner: DatElement::new(info),
            unresolved: Vec::new(),
            behavior_active: false,
            active_page: 0,
        }
    }

    /// The tab table.
    #[must_use]
    pub fn tabs(&self) -> &[TabEntry] {
        &self.tabs
    }

    /// The page shown (dat element id, 0 before the first switch).
    #[must_use]
    pub fn active_page(&self) -> u32 {
        self.active_page
    }

    /// True once [`TabPanel::activate`] ran.
    #[must_use]
    pub fn behavior_active(&self) -> bool {
        self.behavior_active
    }

    /// Entries whose button or page is not in the built tree.
    #[must_use]
    pub fn unresolved(&self) -> &[TabEntry] {
        &self.unresolved
    }

    /// Start tab behaviour: make each tab button take presses (so its
    /// click reaches the panel) and show the default page.
    pub fn activate(&mut self, ui: &mut UiRoot, me: ElementId) {
        if self.behavior_active {
            return;
        }
        self.behavior_active = true;
        self.unresolved.clear();
        let mut default_page = None;
        for entry in self.tabs.clone() {
            let button = ui.find_descendant(me, entry.button);
            let page = ui.find_descendant(me, entry.page);
            if button.is_none() || page.is_none() {
                self.unresolved.push(entry);
            }
            if let Some(b) = button
                && let Some(e) = ui.element_mut(b)
            {
                e.click_through = false;
                e.captures_pointer_drag = true;
            }
            if entry.is_default {
                default_page = Some(entry.page);
            }
        }
        if let Some(page) = default_page {
            self.switch_to(ui, me, page);
        }
    }

    /// Show page `page` (and open its button), hide the others.
    pub fn switch_to(&mut self, ui: &mut UiRoot, me: ElementId, page: u32) {
        if self.active_page == page {
            return;
        }
        let previous = self.active_page;
        for entry in self.tabs.clone() {
            let active = entry.page == page;
            if let Some(p) = ui.find_descendant(me, entry.page) {
                ui.set_visible(p, active);
            }
            if let Some(b) = ui.find_descendant(me, entry.button) {
                set_open(ui, b, active);
            }
        }
        self.active_page = page;
        ui.send_message(me, ElementMessage::TabChanged { previous, page });
    }

    fn tab_of(&self, ui: &UiRoot, me: ElementId, target: Option<ElementId>) -> Option<u32> {
        let mut cur = target;
        while let Some(c) = cur {
            if c == me {
                return None;
            }
            let id = ui.element(c)?.dat_element_id;
            if let Some(entry) = self.tabs.iter().find(|t| t.button == id && id != 0) {
                return Some(entry.page);
            }
            cur = ui.parent(c);
        }
        None
    }
}

/// Open or close a tab button: its Open/Closed state when it has them,
/// else a button's selection.
pub fn set_open(ui: &mut UiRoot, button: ElementId, open: bool) -> bool {
    if ui.set_retail_state(button, if open { states::OPEN } else { states::CLOSED }) {
        return true;
    }
    ui.with::<Button, _>(button, |b, ui| b.set_selected(ui, button, open))
        .is_some()
}

impl Widget for TabPanel {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if self.behavior_active
            && e.kind == kind::CLICK
            && let Some(page) = self.tab_of(ui, me, e.target)
        {
            self.switch_to(ui, me, page);
            return true;
        }
        self.inner.on_event(ui, me, e)
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        self.inner.draw(ui, me, rc);
    }

    fn active_cursor_state_name(&self) -> &str {
        self.inner.active_cursor_state_name()
    }

    fn retail_state(&self) -> Option<u32> {
        self.inner.retail_state()
    }

    fn set_retail_state(&mut self, ui: &mut UiRoot, me: ElementId, state: u32) -> bool {
        self.inner.set_retail_state(ui, me, state)
    }
}
