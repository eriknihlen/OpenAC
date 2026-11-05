//! The connecting screen: the connection and data-update meters, their
//! status texts, the error box and the cancel button, shown on the
//! authored 800x600 canvas while the session connects.

use std::time::{Duration, Instant};

use ac_runtime::session::pregame::{ConnectionStatus, ConnectionView};

use crate::draw::Color;
use crate::import::ImportedLayout;
use crate::root::UiRoot;
use crate::text::{Line, wrap_words};
use crate::tree::{Element, ElementId};
use crate::widgets::text::TextContent;
use crate::widgets::{Button, Meter, Panel, Text};

/// The screen's entry in the interface enumeration (category 5).
pub const ROOT_ENUM: u32 = 0x1000_0001;
/// The screen's root element.
pub const ROOT_ELEMENT_ID: u32 = 0x1000_041A;
/// The connection meter.
pub const CONNECTION_METER_ID: u32 = 0x1000_041E;
/// The data-update meter.
pub const UPDATE_METER_ID: u32 = 0x1000_041F;
/// The connection status text.
pub const CONNECTION_TEXT_ID: u32 = 0x1000_0420;
/// The update status text.
pub const UPDATE_TEXT_ID: u32 = 0x1000_0421;
/// The cancel button.
pub const CANCEL_ID: u32 = 0x1000_041C;
/// The connection text's state while connecting.
pub const CONNECTION_IN_PROGRESS_STATE: u32 = 0x1000_003B;
/// The connection text's state once connected.
pub const CONNECTION_DONE_STATE: u32 = 0x1000_003C;
/// The connection text's state otherwise (Normal).
pub const CONNECTION_IDLE_STATE: u32 = 1;
/// How long the progress shows at least.
pub const MINIMUM_VISIBLE: Duration = Duration::from_secs(2);

/// The fixed-canvas owner number of this screen.
pub(crate) const CANVAS_OWNER: u64 = 1;

/// The connecting screen. See the module documentation.
#[derive(Debug)]
pub struct ConnectionScreen {
    root: ElementId,
    connection_meter: ElementId,
    update_meter: ElementId,
    connection_text: ElementId,
    update_text: ElementId,
    cancel: ElementId,
    error_panel: ElementId,
    error_text: ElementId,
    checking_text: String,
    completed_text: String,
    initial_update_text: TextContent,
    shown: Option<ConnectionView>,
    presentation_completed: bool,
    minimum_visible: Duration,
}

impl ConnectionScreen {
    /// Bind the imported screen (root 0x1000041A) and add it to the screen
    /// root, hidden. `None` (and nothing mounted) when a meter, text or the
    /// cancel button is missing. `checking` and `completed` are the update
    /// text's "looking for data" and "patching done" strings.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        checking: String,
        completed: String,
    ) -> Option<Self> {
        let root = layout.root;
        let is_meter = |ui: &UiRoot, id| ui.widget::<Meter>(id).is_some();
        let is_text = |ui: &UiRoot, id| ui.widget::<Text>(id).is_some();
        if ui.element(root)?.dat_element_id != ROOT_ELEMENT_ID {
            return None;
        }
        let connection_meter = layout
            .find(CONNECTION_METER_ID)
            .filter(|m| is_meter(ui, *m))?;
        let update_meter = layout.find(UPDATE_METER_ID).filter(|m| is_meter(ui, *m))?;
        let connection_text = layout
            .find(CONNECTION_TEXT_ID)
            .filter(|t| is_text(ui, *t))?;
        let update_text = layout.find(UPDATE_TEXT_ID).filter(|t| is_text(ui, *t))?;
        let cancel = layout
            .find(CANCEL_ID)
            .filter(|b| ui.widget::<Button>(*b).is_some())?;
        let (width, height) = {
            let e = ui.element_mut(root)?;
            e.left = 0.0;
            e.top = 0.0;
            e.visible = false;
            e.click_through = false;
            (e.width, e.height)
        };
        // A black backdrop under all the authored art, over the world.
        let mut backdrop = Element::at(0.0, 0.0, width, height);
        backdrop.name = Some("ConnectionBackdrop".to_owned());
        backdrop.z_order = i32::MIN;
        backdrop.click_through = true;
        ui.add(
            root,
            backdrop,
            Panel {
                background_color: Color::BLACK,
                border_color: Color::TRANSPARENT,
                border_thickness: 0.0,
                background_sprite: 0,
            },
        );
        if let Some(b) = ui.widget_mut::<Button>(cancel) {
            b.listeners.click = true;
        }
        for meter in [connection_meter, update_meter] {
            if let Some(m) = ui.widget_mut::<Meter>(meter) {
                m.fill = Some(0.0);
            }
        }
        let (font, outline_color) = ui
            .widget::<Text>(update_text)
            .map(|t| (t.font.clone(), t.outline_color))
            .unwrap_or_default();
        let mut panel = Element::at(30.0, 352.0, 740.0, 104.0);
        panel.visible = false;
        panel.click_through = true;
        let error_panel = ui.add(
            root,
            panel,
            Panel {
                background_color: Color::rgba(0.0, 0.0, 0.0, 0.92),
                border_color: Color::TRANSPARENT,
                border_thickness: 0.0,
                background_sprite: 0,
            },
        );
        let mut text = Element::at(12.0, 12.0, 716.0, 80.0);
        Text::configure(&mut text);
        let error_text = ui.add(error_panel, text, Text::default());
        if let Some(t) = ui.widget_mut::<Text>(error_text) {
            t.font = font;
            t.default_color = Color::WHITE;
            t.outline = true;
            t.outline_color = outline_color;
        }
        let initial_update_text = ui
            .widget::<Text>(update_text)
            .map(|t| t.content().clone())
            .unwrap_or(TextContent::Empty);
        let screen = ui.root();
        ui.add_child(screen, root);
        Some(Self {
            root,
            connection_meter,
            update_meter,
            connection_text,
            update_text,
            cancel,
            error_panel,
            error_text,
            checking_text: checking,
            completed_text: completed,
            initial_update_text,
            shown: None,
            presentation_completed: false,
            minimum_visible: MINIMUM_VISIBLE,
        })
    }

    /// Show the progress for at least `minimum` (tests).
    pub fn set_minimum_visible(&mut self, minimum: Duration) {
        self.minimum_visible = minimum;
    }

    /// The screen's root.
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    /// The cancel button.
    #[must_use]
    pub fn cancel_button(&self) -> ElementId {
        self.cancel
    }

    /// The error text.
    #[must_use]
    pub fn error_text(&self) -> ElementId {
        self.error_text
    }

    /// Whether the screen shows.
    #[must_use]
    pub fn is_visible(&self, ui: &UiRoot) -> bool {
        ui.element(self.root).is_some_and(|e| e.visible)
    }

    /// Follow the connection at `now` (`None`: the session said nothing,
    /// as no connection). The progress shows (unless the session enters
    /// its character by itself) from the connection's start until it is
    /// ready and has shown for the minimum time; a failure or a refused
    /// update shows until the connection starts over.
    pub fn update(&mut self, ui: &mut UiRoot, view: Option<&ConnectionView>, now: Instant) {
        let status = view.map_or(ConnectionStatus::Inactive, |v| v.status);
        let previous = self.shown.as_ref().map(|v| v.status);
        let restarted = self
            .shown
            .as_ref()
            .zip(view)
            .is_some_and(|(a, b)| a.started != b.started);
        if status == ConnectionStatus::Inactive
            || (status == ConnectionStatus::Connecting
                && previous != Some(ConnectionStatus::Connecting))
            || restarted
        {
            self.presentation_completed = false;
        }
        let failure = matches!(
            status,
            ConnectionStatus::Unsupported | ConnectionStatus::Failed
        );
        let mut progress = view.is_some_and(|v| !v.direct_launch)
            && !self.presentation_completed
            && matches!(
                status,
                ConnectionStatus::Connecting
                    | ConnectionStatus::CheckingData
                    | ConnectionStatus::Ready
            );
        if progress
            && status == ConnectionStatus::Ready
            && let Some(v) = view
            && now.saturating_duration_since(v.started) >= self.minimum_visible
        {
            self.presentation_completed = true;
            progress = false;
        }
        if !(failure || progress) {
            if self.is_visible(ui) {
                ui.set_visible(self.root, false);
            }
            ui.revoke_fixed_canvas(CANVAS_OWNER);
            self.shown = view.cloned();
            return;
        }
        if !self.is_visible(ui) {
            ui.set_visible(self.root, true);
            let size = ui
                .element(self.root)
                .map_or((800.0, 600.0), |e| (e.width, e.height));
            // Every pre-game screen authors the same canvas.
            let _ = ui.declare_fixed_canvas(CANVAS_OWNER, size);
            ui.bring_to_front(self.root);
        }
        let Some(view) = view else {
            return;
        };
        if self.shown.as_ref() == Some(view) {
            return;
        }
        self.apply(ui, view);
        self.shown = Some(view.clone());
    }

    fn apply(&mut self, ui: &mut UiRoot, view: &ConnectionView) {
        if let Some(m) = ui.widget_mut::<Meter>(self.connection_meter) {
            m.fill = Some(view.connection_progress);
        }
        if let Some(m) = ui.widget_mut::<Meter>(self.update_meter) {
            m.fill = Some(view.update_progress);
        }
        let state = if view.connection_progress >= 1.0 {
            CONNECTION_DONE_STATE
        } else if view.status == ConnectionStatus::Connecting {
            CONNECTION_IN_PROGRESS_STATE
        } else {
            CONNECTION_IDLE_STATE
        };
        ui.set_retail_state(self.connection_text, state);
        let update = if view.update_progress >= 1.0 {
            Some(self.completed_text.clone())
        } else if view.status == ConnectionStatus::CheckingData {
            Some(self.checking_text.clone())
        } else {
            None
        };
        if let Some(t) = ui.widget_mut::<Text>(self.update_text) {
            match update {
                Some(text) => {
                    let color = t.default_color;
                    t.set_lines(vec![Line::new(text, color)]);
                }
                None => t.set_content(self.initial_update_text.clone()),
            }
        }
        let failure = matches!(
            view.status,
            ConnectionStatus::Unsupported | ConnectionStatus::Failed
        );
        ui.set_visible(self.error_panel, failure);
        let width = ui.element(self.error_text).map_or(716.0, |e| e.width);
        if let Some(t) = ui.widget_mut::<Text>(self.error_text) {
            let error = view.error.clone().unwrap_or_default();
            let font = t.font.clone();
            let lines = match &font {
                Some(f) => wrap_words(&error, |s| f.measure_width(s), width),
                None => wrap_words(&error, |s| s.chars().count() as f32 * 8.0, width),
            };
            let color = t.default_color;
            t.set_lines(lines.into_iter().map(|l| Line::new(l, color)).collect());
        }
    }

    /// Take the screen down (the window goes).
    pub fn dispose(&mut self, ui: &mut UiRoot) {
        ui.revoke_fixed_canvas(CANVAS_OWNER);
        ui.destroy(self.root);
    }
}
