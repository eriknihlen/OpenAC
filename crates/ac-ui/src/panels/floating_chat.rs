//! A floating chat window (1..=4, layout 0x2100005B): its own transcript
//! shown through its own text-class filter, an entry that always talks on
//! the Say channel (commands still work), a send button, the title "Chat N"
//! and a close button that hides the window.
//!
//! The client pushes every chat line into each window's feed; what a
//! window shows is rebuilt from it when its lines, filter, width or font
//! change.

use std::sync::Arc;

use ac_core::chat::{BROADCAST_TARGET_WINDOW, ChatWindowId, ChatWindowState};
use ac_runtime::communication::{ChatChannelKind, ChatLine};

use crate::draw::Color;
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::layout_policy::LayoutPolicy;
use crate::panels::PanelOutput;
use crate::panels::chat::{ChatFeed, ChatSubmission};
use crate::panels::transcript::{TranscriptLayout, TranscriptLine, build_lines};
use crate::root::{ElementMessage, UiOutput, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Button, Field, ScrollSource, Scrollbar, Text};

/// The floating chat windows' layout.
pub const LAYOUT_ID: u32 = 0x2100_005B;
/// Window root (250 x 108).
pub const ROOT_ID: u32 = 0x1000_04F7;
/// The panel holding the transcript.
pub const TRANSCRIPT_PANEL_ID: u32 = 0x1000_0010;
/// The transcript text.
pub const TRANSCRIPT_ID: u32 = 0x1000_0011;
/// The transcript's scrollbar.
pub const TRACK_ID: u32 = 0x1000_0012;
/// The entry row.
pub const INPUT_ROW_ID: u32 = 0x1000_0509;
/// The entry field.
pub const INPUT_ID: u32 = 0x1000_0016;
/// The send button.
pub const SEND_ID: u32 = 0x1000_0019;
/// The title text.
pub const TITLE_BAR_ID: u32 = 0x1000_04D9;
/// The close button.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_052A;

/// The title and send captions' colour.
pub const CAPTION_COLOR: Color = Color::rgba(1.0, 0.92, 0.72, 1.0);

/// A floating chat window's controller.
pub struct FloatingChatController {
    window_id: ChatWindowId,
    window_name: String,
    root: ElementId,
    transcript: ElementId,
    input: ElementId,
    scrollbar: Option<ElementId>,
    send: Option<ElementId>,
    close: Option<ElementId>,
    feed: ChatFeed,
    filters: ChatWindowState,
    layout: TranscriptLayout,
    cached: Option<(u64, u64, f32, u32)>,
    /// How many times the transcript was laid out (reuse check).
    pub transcript_layout_builds: u32,
}

impl std::fmt::Debug for FloatingChatController {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FloatingChatController")
            .field("window_id", &self.window_id)
            .field("lines", &self.feed.lines().len())
            .finish_non_exhaustive()
    }
}

impl FloatingChatController {
    /// Bind floating window `window_id` (1..=4) to an imported layout, the
    /// window registered as `window_name`. `None` (and nothing changed)
    /// when the transcript panel, the entry row, the entry field or the
    /// transcript is missing.
    ///
    /// # Panics
    /// When `window_id` is the main window (a caller bug).
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        window_id: ChatWindowId,
        window_name: &str,
        font: Option<Arc<UiFont>>,
    ) -> Option<Self> {
        assert!(
            window_id != ChatWindowId::MAIN,
            "a floating chat window is 1..=4"
        );
        let input = layout
            .find(INPUT_ID)
            .filter(|i| ui.widget::<Field>(*i).is_some())?;
        layout.find(TRANSCRIPT_PANEL_ID)?;
        layout.find(INPUT_ROW_ID)?;
        let transcript = layout
            .find(TRANSCRIPT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some())?;
        let root = layout.find(ROOT_ID).unwrap_or(layout.root);

        ui.with::<Text, _>(transcript, |t, ui| {
            t.font = font.clone();
            t.centered = false;
            t.right_aligned = false;
            t.one_line = false;
            if let Some(e) = ui.element_mut(transcript) {
                t.set_selectable(e, true);
            }
        });
        if let Some(f) = ui.widget_mut::<Field>(input) {
            f.font = font.clone();
        }
        // The entry stretches with the window.
        let policy = ui.element(input).and_then(|e| e.layout_policy);
        match policy {
            Some(p) => {
                let (l, t, _, b) = p.modes();
                let replaced =
                    LayoutPolicy::new(l, t, 1, b, p.original_child, p.original_parent).ok();
                if let Some(e) = ui.element_mut(input) {
                    e.layout_policy = replaced;
                }
            }
            None => {
                let anchors = ui
                    .element(input)
                    .map_or(AnchorEdges::TOP_LEFT, |e| e.anchors);
                ui.set_anchors(input, anchors | AnchorEdges::RIGHT);
            }
        }

        let scrollbar = layout
            .find(TRACK_ID)
            .filter(|s| ui.widget::<Scrollbar>(*s).is_some());
        if let Some(s) = scrollbar
            && let Some(bar) = ui.widget_mut::<Scrollbar>(s)
        {
            bar.model = ScrollSource::Element(transcript);
        }

        let send = layout
            .find(SEND_ID)
            .filter(|s| ui.widget::<Button>(*s).is_some());
        if let Some(s) = send
            && let Some(b) = ui.widget_mut::<Button>(s)
        {
            b.listeners.click = true;
            b.label = Some("Send".to_owned());
            b.label_font = font.clone();
            b.label_color = CAPTION_COLOR;
        }

        if let Some(title) = layout
            .find(TITLE_BAR_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some())
        {
            let caption = format!("Chat {}", window_id.get());
            if let Some(t) = ui.widget_mut::<Text>(title) {
                t.font = font.clone();
                t.one_line = true;
                t.set_lines(vec![Line::new(caption, CAPTION_COLOR)]);
            }
        }

        let close = layout
            .find(CLOSE_BUTTON_ID)
            .filter(|c| ui.widget::<Button>(*c).is_some());
        if let Some(c) = close
            && let Some(b) = ui.widget_mut::<Button>(c)
        {
            b.listeners.click = true;
        }

        Some(Self {
            window_id,
            window_name: window_name.to_owned(),
            root,
            transcript,
            input,
            scrollbar,
            send,
            close,
            feed: ChatFeed::default(),
            filters: ChatWindowState::new(),
            layout: TranscriptLayout::default(),
            cached: None,
            transcript_layout_builds: 0,
        })
    }

    /// The window's number (1..=4).
    #[must_use]
    pub fn window_id(&self) -> ChatWindowId {
        self.window_id
    }

    /// The window root.
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    /// The transcript text element.
    #[must_use]
    pub fn transcript(&self) -> ElementId {
        self.transcript
    }

    /// The entry field.
    #[must_use]
    pub fn input(&self) -> ElementId {
        self.input
    }

    /// The transcript scrollbar.
    #[must_use]
    pub fn scrollbar(&self) -> Option<ElementId> {
        self.scrollbar
    }

    /// The laid-out transcript (as of the last refresh).
    #[must_use]
    pub fn transcript_layout(&self) -> &TranscriptLayout {
        &self.layout
    }

    /// A chat line arrived.
    pub fn push_chat_line(&mut self, line: &ChatLine) {
        self.feed.push_chat_line(line);
    }

    /// A transcript line arrived.
    pub fn push_line(&mut self, line: TranscriptLine) {
        self.feed.push(line);
    }

    /// The chat windows' text-class filters (this window reads its own).
    pub fn set_filters(&mut self, filters: ChatWindowState) {
        self.filters = filters;
    }

    /// Draw the transcript in another font (it re-wraps); the entry keeps
    /// its own.
    pub fn apply_chat_font(&mut self, ui: &mut UiRoot, font: Arc<UiFont>) {
        if let Some(t) = ui.widget_mut::<Text>(self.transcript) {
            t.font = Some(font);
        }
        self.cached = None;
        self.refresh_transcript(ui);
    }

    /// What a typed line becomes: always the Say channel (OpenAC submits
    /// through the command router with Say as the default).
    #[must_use]
    pub fn submission(text: &str) -> ChatSubmission {
        ChatSubmission {
            text: text.to_owned(),
            channel: ChatChannelKind::Say,
            tell_target: None,
            tell_target_guid: 0,
        }
    }

    fn send(ui: &mut UiRoot, text: &str) {
        if let Some(cmd) = Self::submission(text).to_session_command() {
            ui.emit(UiOutput::Panel(PanelOutput::Session(cmd)));
        }
    }

    /// Lay the transcript out again when its lines, this window's filter,
    /// the width or the font changed. A floating window draws each line in
    /// its one colour: no link colour, no grey timestamp, nothing to click
    /// (OpenAC's floating windows build their lines with no runs).
    pub fn refresh_transcript(&mut self, ui: &mut UiRoot) {
        let Some((width, font, padding, default_color)) = ui
            .widget::<Text>(self.transcript)
            .zip(ui.element(self.transcript))
            .map(|(t, e)| (e.width, t.font.clone(), t.padding, t.default_color))
        else {
            return;
        };
        let max_w = width - 2.0 * padding;
        let window = self.window_id;
        let filter = self.filters.filter(window);
        let font_id = font.as_ref().map_or(0, |f| f.id);
        let key = (self.feed.revision(), filter, max_w, font_id);
        if self.cached == Some(key) {
            return;
        }
        let measure = |s: &str| match &font {
            Some(f) => f.measure_width(s),
            None => s.chars().count() as f32 * 7.0,
        };
        let filters = &self.filters;
        let accept = |t: u32| filters.should_display(window, BROADCAST_TARGET_WINDOW, t);
        let lines: Vec<TranscriptLine> = self
            .feed
            .lines()
            .iter()
            .map(|line| TranscriptLine {
                spans: None,
                ..line.clone()
            })
            .collect();
        self.layout = build_lines(&lines, max_w, &measure, Some(&accept), default_color, None);
        self.cached = Some(key);
        self.transcript_layout_builds += 1;
        let layout = &self.layout;
        if let Some(t) = ui.widget_mut::<Text>(self.transcript) {
            t.set_lines(layout.lines.clone());
            t.line_keys = layout.keys.clone();
            t.line_runs = None;
        }
    }
}

impl PanelController for FloatingChatController {
    fn tick(&mut self, ui: &mut UiRoot, _dt: f64) {
        self.refresh_transcript(ui);
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        match message {
            ElementMessage::Submitted(text) if element == self.input => {
                Self::send(ui, text);
                true
            }
            ElementMessage::Clicked { .. } if Some(element) == self.send => {
                let line = ui.widget_mut::<Field>(self.input).and_then(Field::submit);
                if let Some(line) = line {
                    Self::send(ui, &line);
                }
                true
            }
            ElementMessage::Clicked { .. } if Some(element) == self.close => {
                ui.hide_window(&self.window_name);
                true
            }
            _ => false,
        }
    }
}
