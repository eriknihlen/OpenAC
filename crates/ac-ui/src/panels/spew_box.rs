//! The status box (the "spew box"): short client-local messages in yellow at
//! the top of the screen, newest on top, each for a few seconds.
//!
//! Two root children, as OpenAC mounts it: the text, and an invisible time
//! sink that hears the UI's time pulse every frame ([`Widget::global_time`])
//! and owns the queue ([`SpewBoxState`]). So the box shows and fades with no
//! draw pass needed, and a message queued while no character is in the world
//! is dropped.

use std::sync::Arc;

use ac_core::chat::SpewBoxState;

use crate::UiFont;
use crate::draw::Color;
use crate::geometry::AnchorEdges;
use crate::root::UiRoot;
use crate::text::{Line, VJustify};
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::Text;

/// The font the box is authored with (the 18-pixel face).
pub const RETAIL_FONT_ID: u32 = 0x4000_0001;
/// The box's width, pixels.
pub const WIDTH: f32 = 450.0;
/// The box's height, pixels.
pub const HEIGHT: f32 = 72.0;
/// The box's top, pixels (flush with the top of the screen).
pub const TOP: f32 = 0.0;
/// The text colour.
pub const COLOR: Color = Color::rgba(1.0, 1.0, 0.247, 1.0);

/// A line on show and how long it has left.
#[derive(Debug, Clone, PartialEq)]
pub struct SpewBoxLine {
    /// The text.
    pub text: String,
    /// Seconds left, never below zero.
    pub remaining_lifetime_seconds: f64,
}

/// The lines on show at `now_seconds`, newest first, after moving queued
/// messages on show and dropping expired ones (OpenAC `SpewBoxVM.Lines`).
pub fn lines(state: &mut SpewBoxState, now_seconds: f64) -> Vec<SpewBoxLine> {
    state.tick(now_seconds);
    state
        .visible()
        .iter()
        .map(|e| SpewBoxLine {
            text: e.text.clone(),
            remaining_lifetime_seconds: (e.expires_at_seconds - now_seconds).max(0.0),
        })
        .collect()
}

/// The time sink: the queue, and the text it keeps up to date.
#[derive(Debug)]
pub struct SpewBoxSink {
    text: ElementId,
    /// The queue and the messages on show.
    pub state: SpewBoxState,
    /// A character is in the world (otherwise nothing shows and anything
    /// queued is dropped).
    pub gameplay_active: bool,
}

impl Widget for SpewBoxSink {
    fn global_time(&mut self, ui: &mut UiRoot, _me: ElementId, now_seconds: f64) {
        let width = ui.size().0;
        if let Some(e) = ui.element_mut(self.text) {
            e.left = (width - WIDTH) / 2.0;
        }
        let shown = lines(&mut self.state, now_seconds);
        if !self.gameplay_active && !shown.is_empty() {
            self.state.reset();
        }
        let visible = self.gameplay_active && !shown.is_empty();
        ui.set_visible(self.text, visible);
        let rows = if visible {
            shown
                .into_iter()
                .map(|l| Line::new(l.text, COLOR))
                .collect()
        } else {
            Vec::new()
        };
        if let Some(t) = ui.widget_mut::<Text>(self.text)
            && t.content_lines() != rows
        {
            t.set_lines(rows);
        }
    }

    fn hit_test(&self, _element: &Element, _x: f32, _y: f32) -> bool {
        false
    }
}

/// The status box, mounted on a root. See the module documentation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SpewBoxController {
    text: ElementId,
    sink: ElementId,
}

impl SpewBoxController {
    /// Mount the box on `ui`'s root, hidden until a message arrives,
    /// drawing with `font` (the authored face [`RETAIL_FONT_ID`]).
    pub fn mount(ui: &mut UiRoot, font: Option<Arc<UiFont>>) -> Self {
        let root = ui.root();
        let width = ui.size().0;
        let mut element = Element::at((width - WIDTH) / 2.0, TOP, WIDTH, HEIGHT)
            .with_anchors(AnchorEdges::NONE)
            .with_z_order(i32::MAX)
            .with_visible(false);
        element.name = Some("SpewBox".to_owned());
        Text::configure(&mut element);
        let mut text = Text::default();
        text.font = font;
        text.centered = true;
        text.one_line = false;
        text.vertical_justify = VJustify::Top;
        text.honor_vertical_justification = true;
        text.default_color = COLOR;
        text.outline = true;
        let text = ui.add(root, element, text);
        let mut sink_element = Element::at(0.0, 0.0, 0.0, 0.0).with_anchors(AnchorEdges::NONE);
        sink_element.click_through = true;
        let sink = ui.add(
            root,
            sink_element,
            SpewBoxSink {
                text,
                state: SpewBoxState::new(),
                gameplay_active: true,
            },
        );
        Self { text, sink }
    }

    /// The text element.
    #[must_use]
    pub fn text(&self) -> ElementId {
        self.text
    }

    /// The time sink element.
    #[must_use]
    pub fn sink(&self) -> ElementId {
        self.sink
    }

    /// Queue a message; it shows from the next frame.
    pub fn enqueue(&self, ui: &mut UiRoot, text: &str) {
        if let Some(s) = ui.widget_mut::<SpewBoxSink>(self.sink) {
            s.state.enqueue(text);
        }
    }

    /// Whether a character is in the world.
    pub fn set_gameplay_active(&self, ui: &mut UiRoot, active: bool) {
        if let Some(s) = ui.widget_mut::<SpewBoxSink>(self.sink) {
            s.gameplay_active = active;
        }
    }

    /// The queue and the messages on show.
    #[must_use]
    pub fn state<'a>(&self, ui: &'a UiRoot) -> Option<&'a SpewBoxState> {
        ui.widget::<SpewBoxSink>(self.sink).map(|s| &s.state)
    }

    /// The lines the text shows.
    #[must_use]
    pub fn shown(&self, ui: &UiRoot) -> Vec<Line> {
        ui.widget::<Text>(self.text)
            .map(Text::content_lines)
            .unwrap_or_default()
    }

    /// Take the box off the root.
    pub fn dispose(&self, ui: &mut UiRoot) {
        ui.destroy(self.text);
        ui.destroy(self.sink);
    }
}
