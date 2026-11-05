//! The journal's notes page (0x10000563): the page being read (label,
//! title, notes), its number, the recorded location, the countdown, and
//! the buttons that page through the journal, add a page, record the
//! player's place and start or stop the countdown.
//!
//! The journal is the session's ([`JournalView`]); the page asks for every
//! change with [`JournalCommand`]s, committing what was typed first (on
//! leaving a field, a button, or the tab), and shows the next view.

use ac_core::text::format_duration;
use ac_runtime::communication::{JournalCommand, JournalView};
use ac_runtime::session::SessionCommand;

use crate::host::PanelController;
use crate::panels::PanelOutput;
use crate::panels::items::{bind_click, emit};
use crate::radar::RadarCoordinates;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Button, Field, Text};

/// The page in the journal panel.
pub const PAGE_ID: u32 = 0x1000_0563;
/// Previous page.
pub const PREVIOUS_BUTTON_ID: u32 = 0x1000_0565;
/// Next page.
pub const NEXT_BUTTON_ID: u32 = 0x1000_0566;
/// A new page.
pub const NEW_BUTTON_ID: u32 = 0x1000_0567;
/// The label.
pub const LABEL_FIELD_ID: u32 = 0x1000_0569;
/// The title.
pub const TITLE_FIELD_ID: u32 = 0x1000_056B;
/// The notes.
pub const NOTES_FIELD_ID: u32 = 0x1000_056D;
/// The first page.
pub const FIRST_BUTTON_ID: u32 = 0x1000_056F;
/// The page number.
pub const PAGE_NUMBER_ID: u32 = 0x1000_0570;
/// The last page.
pub const LAST_BUTTON_ID: u32 = 0x1000_0571;
/// The recorded location (authored editable: a field).
pub const LOCATION_TEXT_ID: u32 = 0x1000_0573;
/// Record the player's place.
pub const RECORD_BUTTON_ID: u32 = 0x1000_0574;
/// The countdown's days.
pub const TIMER_DAYS_FIELD_ID: u32 = 0x1000_0576;
/// Its hours.
pub const TIMER_HOURS_FIELD_ID: u32 = 0x1000_0578;
/// Its minutes.
pub const TIMER_MINUTES_FIELD_ID: u32 = 0x1000_057A;
/// The days unit label.
pub const TIMER_DAYS_LABEL_ID: u32 = 0x1000_0577;
/// The hours unit label.
pub const TIMER_HOURS_LABEL_ID: u32 = 0x1000_0579;
/// The minutes unit label.
pub const TIMER_MINUTES_LABEL_ID: u32 = 0x1000_057B;
/// The running countdown.
pub const RUNNING_TIMER_TEXT_ID: u32 = 0x1000_057C;
/// Start or stop the countdown.
pub const START_BUTTON_ID: u32 = 0x1000_057D;

/// The location as the page shows it ("45.6N, 12.3E").
#[must_use]
pub fn location_text(x: f32, y: f32) -> String {
    let c = RadarCoordinates {
        x: f64::from(x),
        y: f64::from(y),
    };
    format!("{}, {}", c.y_text(), c.x_text())
}

/// A countdown field as the page shows it: blank for zero.
#[must_use]
pub fn timer_field_text(value: i32) -> String {
    if value == 0 {
        String::new()
    } else {
        value.to_string()
    }
}

/// A countdown field read back: digits, else zero.
#[must_use]
pub fn parse_timer_field(text: &str) -> i32 {
    text.parse::<i32>().ok().filter(|v| *v >= 0).unwrap_or(0)
}

fn command(ui: &mut UiRoot, c: JournalCommand) {
    emit(ui, PanelOutput::Session(SessionCommand::Journal(c)));
}

/// The notes page's controller.
#[derive(Debug)]
pub struct JournalNotesPageController {
    label: Option<ElementId>,
    title: Option<ElementId>,
    notes: Option<ElementId>,
    timer_days: Option<ElementId>,
    timer_hours: Option<ElementId>,
    timer_minutes: Option<ElementId>,
    page_number: Option<ElementId>,
    location: Option<ElementId>,
    running_timer: Option<ElementId>,
    unit_labels: [Option<ElementId>; 3],
    start: Option<ElementId>,
    buttons: [(u32, Option<ElementId>); 7],
    view: JournalView,
    rendered_revision: Option<u64>,
    focused: Option<ElementId>,
    player_cell: u32,
    now_unix_ms: i64,
}

impl JournalNotesPageController {
    /// Bind to the notes page `page`.
    pub fn bind(ui: &mut UiRoot, page: ElementId) -> Self {
        let field = |ui: &UiRoot, id| {
            ui.find_descendant(page, id)
                .filter(|f| ui.widget::<Field>(*f).is_some())
        };
        let text = |ui: &UiRoot, id| {
            ui.find_descendant(page, id)
                .filter(|t| ui.widget::<Text>(*t).is_some())
        };
        let timer_days = field(ui, TIMER_DAYS_FIELD_ID);
        let timer_hours = field(ui, TIMER_HOURS_FIELD_ID);
        let timer_minutes = field(ui, TIMER_MINUTES_FIELD_ID);
        // The countdown boxes take digits only.
        for f in [timer_days, timer_hours, timer_minutes]
            .into_iter()
            .flatten()
        {
            if let Some(w) = ui.widget_mut::<Field>(f) {
                w.character_filter = Some(Box::new(|c: char| c.is_ascii_digit()));
            }
        }
        let mut buttons = [
            (NEW_BUTTON_ID, None),
            (FIRST_BUTTON_ID, None),
            (LAST_BUTTON_ID, None),
            (PREVIOUS_BUTTON_ID, None),
            (NEXT_BUTTON_ID, None),
            (RECORD_BUTTON_ID, None),
            (START_BUTTON_ID, None),
        ];
        for (id, slot) in &mut buttons {
            *slot = ui.find_descendant(page, *id).filter(|b| bind_click(ui, *b));
        }
        let location = ui
            .find_descendant(page, LOCATION_TEXT_ID)
            .filter(|l| ui.widget::<Field>(*l).is_some() || ui.widget::<Text>(*l).is_some());
        let mut c = Self {
            label: field(ui, LABEL_FIELD_ID),
            title: field(ui, TITLE_FIELD_ID),
            notes: field(ui, NOTES_FIELD_ID),
            timer_days,
            timer_hours,
            timer_minutes,
            page_number: text(ui, PAGE_NUMBER_ID),
            location,
            running_timer: text(ui, RUNNING_TIMER_TEXT_ID),
            unit_labels: [
                ui.find_descendant(page, TIMER_DAYS_LABEL_ID),
                ui.find_descendant(page, TIMER_HOURS_LABEL_ID),
                ui.find_descendant(page, TIMER_MINUTES_LABEL_ID),
            ],
            start: ui
                .find_descendant(page, START_BUTTON_ID)
                .filter(|b| ui.widget::<Button>(*b).is_some()),
            buttons,
            view: JournalView::default(),
            rendered_revision: None,
            focused: None,
            player_cell: 0,
            now_unix_ms: 0,
        };
        c.refresh(ui);
        c
    }

    fn field_text(ui: &UiRoot, id: Option<ElementId>) -> String {
        id.and_then(|f| ui.widget::<Field>(f))
            .map(Field::text)
            .unwrap_or_default()
    }

    fn set_field(ui: &mut UiRoot, id: Option<ElementId>, s: &str) {
        if let Some(f) = id.and_then(|f| ui.widget_mut::<Field>(f)) {
            f.set_text(s);
        }
    }

    fn set_text(ui: &mut UiRoot, id: Option<ElementId>, s: &str) {
        if let Some(t) = id.and_then(|t| ui.widget_mut::<Text>(t)) {
            let color = t.default_color;
            t.set_lines(vec![Line::new(s, color)]);
        }
    }

    /// Commit what was typed on the page being read (its texts and the
    /// countdown's length); nothing with no page.
    pub fn commit(&self, ui: &mut UiRoot) {
        if self.view.current_page == 0 {
            return;
        }
        command(
            ui,
            JournalCommand::Edit {
                label: Self::field_text(ui, self.label),
                title: Self::field_text(ui, self.title),
                notes: Self::field_text(ui, self.notes),
            },
        );
        command(
            ui,
            JournalCommand::SetTimer {
                days: parse_timer_field(&Self::field_text(ui, self.timer_days)),
                hours: parse_timer_field(&Self::field_text(ui, self.timer_hours)),
                minutes: parse_timer_field(&Self::field_text(ui, self.timer_minutes)),
            },
        );
    }

    /// Show `view`: the page again when it changed, else the countdown; a
    /// text field the player left commits.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &JournalView,
        player_cell: u32,
        now_unix_ms: i64,
    ) {
        self.player_cell = player_cell;
        self.now_unix_ms = now_unix_ms;
        let focus = ui.keyboard_focus();
        let texts = [self.label, self.title, self.notes];
        if let Some(was) = self.focused
            && focus != Some(was)
            && texts.contains(&Some(was))
        {
            self.commit(ui);
        }
        self.focused = focus;
        if self.rendered_revision != Some(view.revision) {
            self.view.clone_from(view);
            self.refresh(ui);
        } else {
            self.refresh_timer(ui);
        }
    }

    /// Show the journal as last seen.
    pub fn refresh(&mut self, ui: &mut UiRoot) {
        self.rendered_revision = Some(self.view.revision);
        let page = self.view.current();
        Self::set_field(ui, self.label, &page.label);
        Self::set_field(ui, self.title, &page.title);
        Self::set_field(ui, self.notes, &page.notes);
        Self::set_field(ui, self.timer_days, &timer_field_text(page.timer_days));
        Self::set_field(ui, self.timer_hours, &timer_field_text(page.timer_hours));
        Self::set_field(
            ui,
            self.timer_minutes,
            &timer_field_text(page.timer_minutes),
        );
        let number = if self.view.current_page == 0 {
            String::new()
        } else {
            format!("~ {} ~", self.view.current_page)
        };
        Self::set_text(ui, self.page_number, &number);
        let location = if page.has_location {
            location_text(page.location_x, page.location_y)
        } else {
            String::new()
        };
        if self
            .location
            .is_some_and(|l| ui.widget::<Field>(l).is_some())
        {
            Self::set_field(ui, self.location, &location);
        } else {
            Self::set_text(ui, self.location, &location);
        }
        self.refresh_timer(ui);
    }

    fn remaining(&self) -> f64 {
        self.view.remaining_timer_seconds(self.now_unix_ms)
    }

    /// The countdown's boxes, or its running readout in their place.
    fn refresh_timer(&mut self, ui: &mut UiRoot) {
        let remaining = self.remaining();
        let running = remaining > 0.0;
        for e in [self.timer_days, self.timer_hours, self.timer_minutes]
            .into_iter()
            .chain(self.unit_labels)
            .flatten()
        {
            ui.set_visible(e, !running);
        }
        if let Some(t) = self.running_timer {
            ui.set_visible(t, running);
        }
        let text = if running {
            format_duration(remaining)
        } else {
            String::new()
        };
        Self::set_text(ui, self.running_timer, &text);
        if let Some(b) = self.start.and_then(|b| ui.widget_mut::<Button>(b)) {
            b.label = Some(if running { "Stop" } else { "Start" }.to_owned());
        }
    }

    fn navigate(&self, ui: &mut UiRoot, page: usize) {
        self.commit(ui);
        command(ui, JournalCommand::GotoPage(page));
    }

    fn record_location(&self, ui: &mut UiRoot) {
        if self.player_cell == 0 {
            return;
        }
        let Some(c) = RadarCoordinates::from_cell(self.player_cell) else {
            return;
        };
        command(
            ui,
            JournalCommand::RecordLocation {
                x: c.x as f32,
                y: c.y as f32,
            },
        );
    }

    fn toggle_timer(&self, ui: &mut UiRoot) {
        if self.remaining() > 0.0 {
            command(ui, JournalCommand::ResetTimer);
            return;
        }
        self.commit(ui);
        command(ui, JournalCommand::StartTimer);
    }

    fn clicked(&self, ui: &mut UiRoot, id: u32) {
        let current = self.view.current_page;
        match id {
            NEW_BUTTON_ID => {
                self.commit(ui);
                command(ui, JournalCommand::NewPage);
            }
            FIRST_BUTTON_ID => self.navigate(ui, 1),
            LAST_BUTTON_ID => self.navigate(ui, self.view.pages.len()),
            PREVIOUS_BUTTON_ID => self.navigate(ui, current.saturating_sub(1)),
            NEXT_BUTTON_ID => self.navigate(ui, current + 1),
            RECORD_BUTTON_ID => self.record_location(ui),
            START_BUTTON_ID => self.toggle_timer(ui),
            _ => {}
        }
    }
}

impl PanelController for JournalNotesPageController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let Some(&(id, _)) = self.buttons.iter().find(|(_, e)| *e == Some(element)) else {
            return false;
        };
        if matches!(message, ElementMessage::Clicked { .. }) {
            self.clicked(ui, id);
        }
        true
    }
}
