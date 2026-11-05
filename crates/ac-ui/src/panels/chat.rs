//! The main chat window: the transcript (retail colours per text class,
//! wrapped, selectable, newest at the bottom, its scrollbar), the entry
//! field (typed lines become session commands), the talk-focus menu, the
//! unread indicator, the floating-window indicators and maximize.
//!
//! The controller owns a feed of chat lines the client fills from the
//! session's [`HostEvent::Chat`](ac_runtime::session::HostEvent) events
//! ([`ChatWindowController::push_chat_line`]); everything it shows is
//! rebuilt from that feed.

use std::cell::RefCell;
use std::collections::VecDeque;
use std::rc::Rc;
use std::sync::Arc;

use ac_core::chat::{BROADCAST_TARGET_WINDOW, ChatWindowId, ChatWindowState};
use ac_runtime::communication::{
    ChatChannelKind, ChatEntryCommand, ChatLine, expand_reply_abbreviation,
};
use ac_runtime::session::SessionCommand;
use ac_runtime::session::interface::ChatEntryView;

use crate::event::Key;
use crate::font::UiFont;
use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::layout_policy::LayoutPolicy;
use crate::panels::PanelOutput;
use crate::panels::transcript::{TagRange, TranscriptLayout, TranscriptLine, build_lines};
use crate::root::{ElementMessage, RecallRequest, UiOutput, UiRoot};
use crate::states;
use crate::text::Pos;
use crate::tree::ElementId;
use crate::widgets::{Button, Field, Menu, MenuItem, ScrollSource, Scrollbar, Text};

/// The main chat window's layout.
pub const LAYOUT_ID: u32 = 0x2100_006F;
/// Window root.
pub const ROOT_ID: u32 = 0x1000_0600;
/// The panel holding the transcript.
pub const TRANSCRIPT_PANEL_ID: u32 = 0x1000_0010;
/// The transcript text.
pub const TRANSCRIPT_ID: u32 = 0x1000_0011;
/// "There is text you have not seen" indicator.
pub const UNREAD_INDICATOR_ID: u32 = 0x1000_048C;
/// The transcript's scrollbar.
pub const TRACK_ID: u32 = 0x1000_0012;
/// The entry bar.
pub const INPUT_BAR_ID: u32 = 0x1000_0013;
/// The talk-focus menu.
pub const MENU_ID: u32 = 0x1000_0014;
/// The entry field.
pub const INPUT_ID: u32 = 0x1000_0016;
/// The send button.
pub const SEND_ID: u32 = 0x1000_0019;
/// The maximize/restore button.
pub const MAX_MIN_ID: u32 = 0x1000_046F;
/// The floating chat window 1..=4 indicators.
pub const INDICATOR_IDS: [u32; 4] = [0x1000_0522, 0x1000_0523, 0x1000_0524, 0x1000_0525];
/// The "_Locked" border-art twins (shown only while the UI is locked).
pub const LOCKED_TWIN_IDS: [u32; 8] = [
    0x1000_0693,
    0x1000_0694,
    0x1000_0695,
    0x1000_0696,
    0x1000_0697,
    0x1000_0698,
    0x1000_0699,
    0x1000_069A,
];

/// The talk-focus menu's button face.
pub const MENU_NORMAL: u32 = 0x0600_4D65;
/// Pressed.
pub const MENU_PRESSED: u32 = 0x0600_4D66;
/// The popup fill.
pub const MENU_POPUP_BG: u32 = 0x0600_124C;
/// An entry row.
pub const MENU_ITEM_ROW: u32 = 0x0600_124E;
/// The selected entry row.
pub const MENU_ITEM_SELECTED: u32 = 0x0600_124D;

/// Menu payload of "Squelch (ignore) <selected>".
pub const SQUELCH_PAYLOAD: u64 = 1000;
/// Menu payload of "Tell to <selected>".
pub const TELL_TO_SELECTED_PAYLOAD: u64 = 1001;

/// The channel entries of the talk-focus menu: string key, authored
/// caption, channel. Their payload is their index.
pub const CHANNEL_ITEMS: [(&str, &str, ChatChannelKind); 12] = [
    ("ID_Chat_TellToAll", "Chat to All", ChatChannelKind::Say),
    (
        "ID_Chat_TellToFellows",
        "Tell to Fellows",
        ChatChannelKind::Fellowship,
    ),
    (
        "ID_Chat_TellToGeneral",
        "Tell to General Chat",
        ChatChannelKind::General,
    ),
    (
        "ID_Chat_TellToLFG",
        "Tell to LFG Chat",
        ChatChannelKind::Lfg,
    ),
    (
        "ID_Chat_TellToSociety",
        "Tell to Society Chat",
        ChatChannelKind::Society,
    ),
    (
        "ID_Chat_TellToMonarch",
        "Tell to Monarch",
        ChatChannelKind::Monarch,
    ),
    (
        "ID_Chat_TellToPatron",
        "Tell to Patron",
        ChatChannelKind::Patron,
    ),
    (
        "ID_Chat_TellToVassals",
        "Tell to Vassals",
        ChatChannelKind::Vassals,
    ),
    (
        "ID_Chat_TellToAllegiance",
        "Tell to Allegiance",
        ChatChannelKind::Allegiance,
    ),
    (
        "ID_Chat_TellToTrade",
        "Tell to Trade Chat",
        ChatChannelKind::Trade,
    ),
    (
        "ID_Chat_TellToRoleplay",
        "Tell to Roleplay Chat",
        ChatChannelKind::Roleplay,
    ),
    (
        "ID_Chat_TellToOlthoi",
        "Tell to Olthoi Chat",
        ChatChannelKind::Olthoi,
    ),
];

/// The channels a line can go to from the menu now.
#[must_use]
pub fn channel_available(kind: ChatChannelKind) -> bool {
    matches!(
        kind,
        ChatChannelKind::Say
            | ChatChannelKind::General
            | ChatChannelKind::Trade
            | ChatChannelKind::Lfg
    )
}

/// The talk-focus button's short caption for a channel: string key and
/// authored caption.
#[must_use]
pub fn channel_button_caption(kind: ChatChannelKind) -> (&'static str, &'static str) {
    match kind {
        ChatChannelKind::Tell => ("ID_Chat_ChatTargetMenuSelected", "Tell"),
        ChatChannelKind::General => ("ID_Chat_ChatTargetMenuGeneral", "Gen"),
        ChatChannelKind::Trade => ("ID_Chat_ChatTargetMenuTrade", "Trade"),
        ChatChannelKind::Lfg => ("ID_Chat_ChatTargetMenuLFG", "LFG"),
        ChatChannelKind::Fellowship => ("ID_Chat_ChatTargetMenuFellows", "Fell"),
        ChatChannelKind::Allegiance => ("ID_Chat_ChatTargetMenuAllegiance", "Alg"),
        ChatChannelKind::Patron => ("ID_Chat_ChatTargetMenuPatron", "Pat"),
        ChatChannelKind::Vassals => ("ID_Chat_ChatTargetMenuVassals", "Vas"),
        ChatChannelKind::Monarch => ("ID_Chat_ChatTargetMenuMonarch", "Mon"),
        ChatChannelKind::Roleplay => ("ID_Chat_ChatTargetMenuRoleplay", "RP"),
        ChatChannelKind::Society => ("ID_Chat_ChatTargetMenuSociety", "Soc"),
        ChatChannelKind::Olthoi => ("ID_Chat_ChatTargetMenuOlthoi", "Olt"),
        _ => ("ID_Chat_ChatTargetMenu", "Chat"),
    }
}

/// Resolves an interface string by its key (`ID_Chat_...`).
pub type StringLookup = Box<dyn Fn(&str) -> Option<String>>;

/// A line typed into the entry, with where it goes by default.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatSubmission {
    /// The line as typed.
    pub text: String,
    /// The talk-focus channel.
    pub channel: ChatChannelKind,
    /// The tell target when the focus is a tell.
    pub tell_target: Option<String>,
    /// That target's object id (0: by name).
    pub tell_target_guid: u32,
}

impl ChatSubmission {
    /// The session command for the line: the line as typed with the talk
    /// focus as the router's default channel and tell target (the router
    /// decides between verbs, emotes and plain lines); `None` for a blank
    /// line.
    #[must_use]
    pub fn to_session_command(&self) -> Option<SessionCommand> {
        if self.text.trim().is_empty() {
            return None;
        }
        Some(SessionCommand::ChatInput {
            text: self.text.clone(),
            channel: self.channel,
            tell_target: self.tell_target.clone(),
            tell_target_guid: self.tell_target_guid,
        })
    }
}

/// The chat lines a chat window shows, newest last.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatFeed {
    lines: VecDeque<TranscriptLine>,
    capacity: usize,
    revision: u64,
}

impl Default for ChatFeed {
    fn default() -> Self {
        Self {
            lines: VecDeque::new(),
            capacity: 1000,
            revision: 0,
        }
    }
}

impl ChatFeed {
    /// The lines, oldest first.
    #[must_use]
    pub fn lines(&self) -> &VecDeque<TranscriptLine> {
        &self.lines
    }

    /// Bumped on every change.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Add a line (the oldest drop past the capacity).
    pub fn push(&mut self, line: TranscriptLine) {
        self.lines.push_back(line);
        while self.lines.len() > self.capacity {
            self.lines.pop_front();
        }
        self.revision += 1;
    }

    /// Add a session chat line; markup in its text becomes clickable spans.
    pub fn push_chat_line(&mut self, line: &ChatLine) {
        self.push(transcript_line(line));
    }

    /// Remove every line.
    pub fn clear(&mut self) {
        self.lines.clear();
        self.revision += 1;
    }
}

/// A session chat line as the transcript gets it: its tags and timestamp
/// are the spans the session split it into, and its text is the text the
/// session shows (tags already hidden). The window never reads it again.
#[must_use]
pub fn transcript_line(line: &ChatLine) -> TranscriptLine {
    TranscriptLine {
        spans: line.spans.clone(),
        ..TranscriptLine::plain(line.sequence, line.log_text_type, line.text.clone())
    }
}

/// What the chat window needs from the rest of the client besides its
/// lines and the chat entry.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChatContext {
    /// The selected object's name and id, when one is selected.
    pub selected_target: Option<(String, u32)>,
    /// Keep the entry focused after a line is sent.
    pub stay_in_chat_mode: bool,
}

/// The main chat window's controller.
pub struct ChatWindowController {
    root: ElementId,
    transcript: ElementId,
    input: ElementId,
    scrollbar: Option<ElementId>,
    menu: Option<ElementId>,
    send: Option<ElementId>,
    max_min: Option<ElementId>,
    unread: Option<ElementId>,
    indicators: [Option<ElementId>; 4],
    indicator_clicks: bool,
    strings: Option<StringLookup>,
    feed: ChatFeed,
    filters: ChatWindowState,
    context: ChatContext,
    last_teller: Rc<RefCell<Option<String>>>,
    /// The session's chat entry as last heard (the window's field is a view
    /// of it: OpenAC `RuntimeChatEntryOwner`).
    entry: ChatEntryView,
    /// The entry's shown revision the field last showed.
    shown_revision: Option<u64>,
    channel: ChatChannelKind,
    tell_target: Option<(String, u32)>,
    layout: TranscriptLayout,
    cached: Option<(u64, u64, f32, u32)>,
    has_unseen_text: bool,
    flash_started: bool,
    maximized: bool,
    normal_top: f32,
    normal_height: f32,
    /// How many times the transcript was laid out (reuse check).
    pub transcript_layout_builds: u32,
}

impl std::fmt::Debug for ChatWindowController {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ChatWindowController")
            .field("root", &self.root)
            .field("channel", &self.channel)
            .field("lines", &self.feed.lines.len())
            .finish_non_exhaustive()
    }
}

impl ChatWindowController {
    /// Bind to an imported main chat window. `None` (and nothing changed)
    /// when the layout lacks the transcript panel, the entry bar or the
    /// entry field.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        font: Option<Arc<UiFont>>,
        strings: Option<StringLookup>,
    ) -> Option<Self> {
        let input = layout
            .find(INPUT_ID)
            .filter(|i| ui.widget::<Field>(*i).is_some())?;
        layout.find(TRANSCRIPT_PANEL_ID)?;
        layout.find(INPUT_BAR_ID)?;
        let transcript = layout
            .find(TRANSCRIPT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some())?;
        let root = layout.find(ROOT_ID).unwrap_or(layout.root);

        for id in LOCKED_TWIN_IDS {
            if let Some(twin) = layout.find(id) {
                ui.set_visible(twin, false);
            }
        }
        let mut indicators = [None; 4];
        for (slot, id) in indicators.iter_mut().zip(INDICATOR_IDS) {
            if let Some(b) = layout.find(id)
                && let Some(button) = ui.widget_mut::<Button>(b)
            {
                button.suppress_self_toggle = true;
                *slot = Some(b);
            }
        }

        ui.with::<Text, _>(transcript, |t, ui| {
            t.font = font.clone();
            t.centered = false;
            t.right_aligned = false;
            t.one_line = false;
            if let Some(e) = ui.element_mut(transcript) {
                t.set_selectable(e, true);
            }
            t.emits_char_clicks = true;
        });

        let last_teller = Rc::new(RefCell::new(None::<String>));
        let teller = last_teller.clone();
        if let Some(f) = ui.widget_mut::<Field>(input) {
            f.font = font.clone();
            // The session's chat entry sends the line, remembers it and
            // keeps the draft; the field keeps the caret, the selection and
            // the drawing.
            f.record_history = false;
            f.shared_history = true;
            f.text_replacer = Some(Box::new(move |text: &str| {
                expand_reply_abbreviation(text, teller.borrow().as_deref())
            }));
        }
        // The entry stretches with the window: its right edge follows the
        // parent's growth.
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

        let menu = layout
            .find(MENU_ID)
            .filter(|m| ui.widget::<Menu>(*m).is_some());
        if let Some(m) = menu
            && let Some(w) = ui.widget_mut::<Menu>(m)
        {
            if w.font.is_none() {
                w.font = font.clone();
            }
            w.normal_sprite = MENU_NORMAL;
            w.pressed_sprite = MENU_PRESSED;
            w.popup_bg_sprite = MENU_POPUP_BG;
            w.item_normal_sprite = MENU_ITEM_ROW;
            w.item_highlight_sprite = MENU_ITEM_SELECTED;
        }

        let send = layout
            .find(SEND_ID)
            .filter(|s| ui.widget::<Button>(*s).is_some());
        if let Some(s) = send
            && let Some(b) = ui.widget_mut::<Button>(s)
        {
            b.listeners.click = true;
            b.label = Some("Send".to_owned());
            if b.label_font.is_none() {
                b.label_font = font.clone();
            }
        }
        let max_min = layout
            .find(MAX_MIN_ID)
            .filter(|s| ui.widget::<Button>(*s).is_some());
        if let Some(m) = max_min
            && let Some(b) = ui.widget_mut::<Button>(m)
        {
            b.listeners.click = true;
        }

        let mut c = Self {
            root,
            transcript,
            input,
            scrollbar,
            menu,
            send,
            max_min,
            unread: layout.find(UNREAD_INDICATOR_ID),
            indicators,
            indicator_clicks: false,
            strings,
            feed: ChatFeed::default(),
            filters: ChatWindowState::new(),
            context: ChatContext::default(),
            last_teller,
            entry: ChatEntryView::default(),
            shown_revision: None,
            channel: ChatChannelKind::Say,
            tell_target: None,
            layout: TranscriptLayout::default(),
            cached: None,
            has_unseen_text: false,
            flash_started: false,
            maximized: false,
            normal_top: 0.0,
            normal_height: 0.0,
            transcript_layout_builds: 0,
        };
        if let Some(u) = c.unread {
            c.set_unread_indicator_state(ui, false);
            if let Some(b) = ui.widget_mut::<Button>(u) {
                b.listeners.click = true;
            }
        }
        c.rebuild_menu(ui);
        Some(c)
    }

    // ── Accessors ───────────────────────────────────────────────────────

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

    /// The talk-focus menu.
    #[must_use]
    pub fn menu(&self) -> Option<ElementId> {
        self.menu
    }

    /// The unread indicator.
    #[must_use]
    pub fn unread_indicator(&self) -> Option<ElementId> {
        self.unread
    }

    /// The talk-focus channel.
    #[must_use]
    pub fn channel(&self) -> ChatChannelKind {
        self.channel
    }

    /// The tell target picked from the selection.
    #[must_use]
    pub fn tell_target(&self) -> Option<&(String, u32)> {
        self.tell_target.as_ref()
    }

    /// True while the window is maximized.
    #[must_use]
    pub fn is_maximized(&self) -> bool {
        self.maximized
    }

    /// The feed of lines.
    #[must_use]
    pub fn feed(&self) -> &ChatFeed {
        &self.feed
    }

    /// The laid-out transcript (as of the last refresh).
    #[must_use]
    pub fn transcript_layout(&self) -> &TranscriptLayout {
        &self.layout
    }

    /// True while text arrived the player has not scrolled down to.
    #[must_use]
    pub fn has_unseen_text(&self) -> bool {
        self.has_unseen_text
    }

    // ── Inputs from the client ──────────────────────────────────────────

    /// A chat line arrived.
    pub fn push_chat_line(&mut self, line: &ChatLine) {
        self.feed.push_chat_line(line);
    }

    /// A transcript line arrived.
    pub fn push_line(&mut self, line: TranscriptLine) {
        self.feed.push(line);
    }

    /// The window's text-class filters.
    pub fn set_filters(&mut self, filters: ChatWindowState) {
        self.filters = filters;
    }

    /// The selection and the chat-mode option.
    pub fn set_context(&mut self, context: ChatContext) {
        self.context = context;
    }

    /// The session's chat entry as it stands (OpenAC: the window's field
    /// is a view of `RuntimeChatEntryOwner`): its talk focus, the reply
    /// targets, and a draft that changed other than by this window's
    /// typing (a recalled or staged line, a send), which the field shows;
    /// a staged line takes the keyboard.
    pub fn set_entry(&mut self, ui: &mut UiRoot, view: &ChatEntryView) {
        *self.last_teller.borrow_mut() = view.last_incoming_tell_sender.clone();
        self.channel = view.channel;
        self.tell_target = view
            .tell_target
            .clone()
            .map(|name| (name, view.tell_target_guid));
        if self.shown_revision != Some(view.shown_revision) {
            self.shown_revision = Some(view.shown_revision);
            if view.composed {
                ui.set_keyboard_focus(Some(self.input));
            }
            if let Some(f) = ui.widget_mut::<Field>(self.input)
                && f.text() != view.draft
            {
                f.show_text(&view.draft);
            }
        }
        self.entry = view.clone();
        self.rebuild_menu(ui);
    }

    /// The session's chat entry as last heard.
    #[must_use]
    pub fn entry(&self) -> &ChatEntryView {
        &self.entry
    }

    /// Draw the transcript in another font (it re-wraps).
    pub fn apply_chat_font(&mut self, ui: &mut UiRoot, font: Arc<UiFont>) {
        if let Some(t) = ui.widget_mut::<Text>(self.transcript) {
            t.font = Some(font);
        }
        self.refresh_transcript(ui);
    }

    // ── Strings and the menu ────────────────────────────────────────────

    fn s(&self, key: &str, fallback: &str) -> String {
        self.strings
            .as_ref()
            .and_then(|f| f(key))
            .unwrap_or_else(|| fallback.to_owned())
    }

    /// The talk-focus button's caption for the current channel.
    #[must_use]
    pub fn channel_button_label(&self) -> String {
        let (key, fallback) = channel_button_caption(self.channel);
        self.s(key, fallback)
    }

    fn rebuild_menu(&mut self, ui: &mut UiRoot) {
        let Some(m) = self.menu else {
            return;
        };
        let target = self
            .context
            .selected_target
            .as_ref()
            .map(|(n, _)| n.clone());
        let mut items = vec![
            MenuItem::new(
                match &target {
                    None => self.s(
                        "ID_Chat_SquelchSelectedNoSelection",
                        "Squelch (ignore) Selected",
                    ),
                    Some(t) => format!(
                        "{}{t}",
                        self.s("ID_Chat_SquelchSelected", "Squelch (ignore) ")
                    ),
                },
                SQUELCH_PAYLOAD,
            )
            .with_enabled(target.is_some()),
            MenuItem::new(
                match &target {
                    None => self.s("ID_Chat_TellToSelectedNoSelection", "Tell to Selected"),
                    Some(t) => format!("{}{t}", self.s("ID_Chat_TellToSelected", "Tell to ")),
                },
                TELL_TO_SELECTED_PAYLOAD,
            )
            .with_enabled(target.is_some()),
        ];
        for (i, (key, fallback, ch)) in CHANNEL_ITEMS.iter().enumerate() {
            items.push(
                MenuItem::new(self.s(key, fallback), i as u64).with_enabled(channel_available(*ch)),
            );
        }
        let label = self.channel_button_label();
        let selected = self.menu_selected_payload();
        if let Some(w) = ui.widget_mut::<Menu>(m) {
            w.items = items;
            w.button_label = label;
            w.selected = selected;
        }
    }

    fn menu_selected_payload(&self) -> Option<u64> {
        if self.channel == ChatChannelKind::Tell && self.tell_target.is_some() {
            return Some(TELL_TO_SELECTED_PAYLOAD);
        }
        CHANNEL_ITEMS
            .iter()
            .position(|(_, _, c)| *c == self.channel)
            .map(|i| i as u64)
    }

    fn on_menu_selected(&mut self, ui: &mut UiRoot, payload: u64) {
        match payload {
            TELL_TO_SELECTED_PAYLOAD => {
                if let Some((name, guid)) = self.context.selected_target.clone() {
                    // Speaking to whoever is selected aims at the object.
                    self.channel = ChatChannelKind::Tell;
                    self.tell_target = Some((name.clone(), guid));
                    entry_command(ui, ChatEntryCommand::SetTellTarget { name, guid });
                }
            }
            SQUELCH_PAYLOAD => {
                if let Some((name, _)) = self.context.selected_target.clone() {
                    ui.emit(UiOutput::Panel(PanelOutput::Session(
                        SessionCommand::Squelch {
                            add: true,
                            name,
                            account: false,
                        },
                    )));
                }
            }
            i => {
                if let Some((_, _, ch)) = CHANNEL_ITEMS.get(i as usize) {
                    self.channel = *ch;
                    self.tell_target = None;
                    entry_command(ui, ChatEntryCommand::SetChannel(*ch));
                }
            }
        }
        self.rebuild_menu(ui);
    }

    // ── Sending ─────────────────────────────────────────────────────────

    /// A line typed into the entry goes to the session's chat entry, which
    /// routes it with its talk focus, remembers it and clears the draft
    /// (OpenAC `RuntimeChatEntryOwner.Submit`).
    fn send(ui: &mut UiRoot, text: &str) {
        entry_command(ui, ChatEntryCommand::Submit(Some(text.to_owned())));
    }

    fn submit_field(&mut self, ui: &mut UiRoot) {
        let (line, requests) = ui
            .widget_mut::<Field>(self.input)
            .map(|f| (f.submit(), f.take_recall_requests()))
            .unwrap_or_default();
        self.send_recall_requests(ui, requests);
        if let Some(line) = line {
            Self::send(ui, &line);
        }
        entry_command(ui, ChatEntryCommand::SetDraft(self.field_text(ui)));
    }

    fn field_text(&self, ui: &UiRoot) -> String {
        ui.widget::<Field>(self.input)
            .map(Field::text)
            .unwrap_or_default()
    }

    fn send_recall_requests(&self, ui: &mut UiRoot, requests: Vec<RecallRequest>) {
        for request in requests {
            entry_command(
                ui,
                match request {
                    RecallRequest::Previous => ChatEntryCommand::RecallPrevious,
                    RecallRequest::Next => ChatEntryCommand::RecallNext,
                    RecallRequest::Stop => ChatEntryCommand::ResetRecall,
                },
            );
        }
    }

    /// Put `text` in the entry as the player's own (OpenAC `UiField.SetText`:
    /// the walk through sent lines stops and the entry's draft follows).
    fn set_entry_text(&mut self, ui: &mut UiRoot, text: &str) {
        let requests = ui
            .widget_mut::<Field>(self.input)
            .map(|f| {
                f.set_text(text);
                f.take_recall_requests()
            })
            .unwrap_or_default();
        self.send_recall_requests(ui, requests);
        entry_command(ui, ChatEntryCommand::SetDraft(self.field_text(ui)));
    }

    // ── Entry focus ─────────────────────────────────────────────────────

    /// Start a tell to `name`: "@tell name, " in the entry, focused.
    pub fn start_tell(&mut self, ui: &mut UiRoot, name: &str) {
        self.set_entry_text(ui, &format!("@tell {name}, "));
        ui.set_keyboard_focus(Some(self.input));
    }

    /// Reply to `name` (nothing without one).
    pub fn start_reply(&mut self, ui: &mut UiRoot, name: Option<&str>) {
        if let Some(n) = name.filter(|n| !n.is_empty()) {
            self.start_tell(ui, n);
        }
    }

    /// Start a command: "/" in the entry, focused.
    pub fn start_command(&mut self, ui: &mut UiRoot) {
        self.set_entry_text(ui, "/");
        ui.set_keyboard_focus(Some(self.input));
    }

    /// Focus the entry and select what is in it; the key that did it does
    /// not type into it.
    pub fn enter_chat_mode(&mut self, ui: &mut UiRoot, physical_key: Option<Key>) {
        ui.set_keyboard_focus(Some(self.input));
        if let Some(k) = physical_key {
            ui.suppress_physical_key_until_release(k);
        }
        if let Some(f) = ui.widget_mut::<Field>(self.input) {
            f.select_all_text();
        }
    }

    /// Focus the entry, or give focus up when it has it.
    pub fn toggle_chat_entry(&mut self, ui: &mut UiRoot, physical_key: Option<Key>) {
        let next = (ui.keyboard_focus() != Some(self.input)).then_some(self.input);
        ui.set_keyboard_focus(next);
        if let Some(k) = physical_key {
            ui.suppress_physical_key_until_release(k);
        }
    }

    // ── Transcript ──────────────────────────────────────────────────────

    /// Lay the transcript out again when its lines, filter, width or font
    /// changed; new text while scrolled up raises the unread flag.
    pub fn refresh_transcript(&mut self, ui: &mut UiRoot) {
        let Some((width, font, padding, default_color, tag_color, at_end)) = ui
            .widget::<Text>(self.transcript)
            .zip(ui.element(self.transcript))
            .map(|(t, e)| {
                (
                    e.width,
                    t.font.clone(),
                    t.padding,
                    t.default_color,
                    t.tag_color,
                    t.scroll.at_end(),
                )
            })
        else {
            return;
        };
        let max_w = width - 2.0 * padding;
        let filter = self.filters.filter(ChatWindowId::MAIN);
        let font_id = font.as_ref().map_or(0, |f| f.id);
        let revision = self.feed.revision;
        if self.cached == Some((revision, filter, max_w, font_id)) {
            return;
        }
        if self.cached.is_some_and(|(r, _, _, _)| r != revision) && !at_end {
            self.has_unseen_text = true;
        }
        let measure = |s: &str| match &font {
            Some(f) => f.measure_width(s),
            None => s.chars().count() as f32 * 7.0,
        };
        let filters = &self.filters;
        let accept =
            |t: u32| filters.should_display(ChatWindowId::MAIN, BROADCAST_TARGET_WINDOW, t);
        let lines: Vec<TranscriptLine> = self.feed.lines.iter().cloned().collect();
        self.layout = build_lines(
            &lines,
            max_w,
            &measure,
            Some(&accept),
            default_color,
            tag_color,
        );
        self.cached = Some((revision, filter, max_w, font_id));
        self.transcript_layout_builds += 1;
        let layout = &self.layout;
        if let Some(t) = ui.widget_mut::<Text>(self.transcript) {
            t.set_lines(layout.lines.clone());
            t.line_keys = layout.keys.clone();
            t.line_runs = Some(
                layout
                    .runs
                    .iter()
                    .map(|r| r.clone().unwrap_or_default())
                    .collect(),
            );
        }
    }

    /// A click on transcript character `pos`: a coordinate tag reports the
    /// place, a tell tag's name starts a tell, any other tag does nothing.
    /// The tell starts whether or not the entry is focused. True when
    /// handled.
    pub fn handle_tag_click(&mut self, ui: &mut UiRoot, pos: Pos) -> bool {
        let Some(Some(ranges)) = self.layout.tags.get(pos.line).cloned() else {
            return false;
        };
        for (start, len, tag) in ranges {
            if pos.col < start || pos.col >= start + len {
                continue;
            }
            if let Some((east_west, north_south)) = tag.coordinate() {
                let text = format!(
                    "{}{}, {}{}",
                    trim_float(north_south),
                    if north_south < 0.0 { 'S' } else { 'N' },
                    trim_float(east_west),
                    if east_west < 0.0 { 'W' } else { 'E' },
                );
                ui.emit(UiOutput::Panel(PanelOutput::CoordinateLinkClicked {
                    east_west,
                    north_south,
                    text,
                }));
                return true;
            }
            // Only a tell tag naming someone starts a tell; a click on any
            // other tag does nothing.
            if !tag.is_tell() {
                continue;
            }
            let Some((_, name)) = tag.iid_string() else {
                continue;
            };
            if name.is_empty() {
                continue;
            }
            let name = name.to_owned();
            self.start_tell(ui, &name);
            return true;
        }
        false
    }

    /// The tag ranges of transcript line `line`.
    #[must_use]
    pub fn tags_of_line(&self, line: usize) -> Option<&[TagRange]> {
        self.layout.tags.get(line).and_then(|t| t.as_deref())
    }

    /// Jump to the newest text and clear the unread flag.
    pub fn scroll_to_newest_and_clear_unread(&mut self, ui: &mut UiRoot) {
        if let Some(t) = ui.widget_mut::<Text>(self.transcript) {
            t.scroll.scroll_to_end();
        }
        self.has_unseen_text = false;
    }

    /// Force the unread flag (tests, restored sessions).
    pub fn set_unread(&mut self, unread: bool) {
        self.has_unseen_text = unread;
    }

    /// Show the unread indicator while unseen text waits below; reaching
    /// the bottom clears it.
    pub fn update_unread_indicator(&mut self, ui: &mut UiRoot) {
        if ui
            .widget::<Text>(self.transcript)
            .is_some_and(|t| t.scroll.at_end())
        {
            self.has_unseen_text = false;
        }
        let unread = self.has_unseen_text;
        self.set_unread_indicator_state(ui, unread);
    }

    fn set_unread_indicator_state(&mut self, ui: &mut UiRoot, unread: bool) {
        let Some(u) = self.unread else {
            return;
        };
        if !unread {
            ui.set_visible(u, false);
            self.flash_started = false;
            return;
        }
        if !self.flash_started {
            // Start the authored flash once; restarting it every frame
            // would hold it on its first frame.
            self.flash_started = true;
            ui.set_visible(u, true);
            ui.set_retail_state(u, states::NORMAL);
            return;
        }
        if ui
            .widget::<Button>(u)
            .is_some_and(|b| b.active_state() == "Ghosted")
        {
            ui.set_visible(u, false);
        }
    }

    // ── Floating-window indicators ──────────────────────────────────────

    /// Light (`open`) or dim indicator `window_id` (1..=4).
    ///
    /// # Panics
    /// When `window_id` is outside 1..=4 (a caller bug).
    pub fn set_indicator_open(&mut self, ui: &mut UiRoot, window_id: usize, open: bool) {
        assert!(
            (1..=4).contains(&window_id),
            "chat window indicator {window_id} is outside 1..=4"
        );
        if let Some(b) = self.indicators[window_id - 1] {
            ui.set_retail_state(
                b,
                if open {
                    states::HIGHLIGHT
                } else {
                    states::NORMAL
                },
            );
        }
    }

    /// Indicator clicks toggle the floating windows
    /// ([`PanelOutput::ToggleFloatingChat`]).
    pub fn bind_indicator_clicks(&mut self, ui: &mut UiRoot) {
        self.indicator_clicks = true;
        for b in self.indicators.iter().flatten() {
            if let Some(w) = ui.widget_mut::<Button>(*b) {
                w.listeners.click = true;
            }
        }
    }

    // ── Maximize ────────────────────────────────────────────────────────

    /// Maximize (grow by half the screen, up or down as there is room) or
    /// restore the window.
    pub fn toggle_maximize(&mut self, ui: &mut UiRoot) {
        let Some((name, frame)) = ui.window_of(self.root).map(|w| (w.name.clone(), w.frame)) else {
            return;
        };
        let parent_h = ui
            .parent(frame)
            .and_then(|p| ui.element(p))
            .map_or(0.0, |p| p.height);
        if parent_h <= 0.0 {
            return;
        }
        let Some((left, top, width, height, min_h, max_h)) = ui
            .element(frame)
            .map(|e| (e.left, e.top, e.width, e.height, e.min_height, e.max_height))
        else {
            return;
        };
        if self.maximized {
            let restored_h = self.normal_height.clamp(min_h, max_h.max(min_h));
            let restored_top = self.normal_top.clamp(0.0, (parent_h - min_h).max(0.0));
            self.maximized = false;
            if let Some(b) = self.max_min {
                ui.set_retail_state(b, states::MINIMIZED);
            }
            ui.resize_window(&name, width, restored_h);
            ui.move_window(&name, left, restored_top);
            return;
        }
        self.normal_top = top;
        self.normal_height = height;
        let expansion = parent_h / 2.0;
        let mut target_h = (height + expansion)
            .min(parent_h)
            .clamp(min_h, max_h.max(min_h));
        let grow_up = top + target_h > parent_h || top >= parent_h / 2.0;
        let target_top = if grow_up {
            (top - (target_h - height)).max(0.0)
        } else {
            top
        };
        target_h = target_h.min(parent_h - target_top);
        self.maximized = true;
        if let Some(b) = self.max_min {
            ui.set_retail_state(b, states::MAXIMIZED);
        }
        ui.resize_window(&name, width, target_h);
        ui.move_window(&name, left, target_top);
    }
}

/// A request to the session's chat entry.
fn entry_command(ui: &mut UiRoot, command: ChatEntryCommand) {
    ui.emit(UiOutput::Panel(PanelOutput::Session(
        SessionCommand::ChatEntry(command),
    )));
}

/// A coordinate as the link text reads it: up to three decimals, no
/// trailing zeros.
fn trim_float(v: f64) -> String {
    let s = format!("{:.3}", v.abs());
    let s = s.trim_end_matches('0').trim_end_matches('.');
    if s.is_empty() {
        "0".to_owned()
    } else {
        s.to_owned()
    }
}

impl PanelController for ChatWindowController {
    fn capture_window_state(&self, _ui: &UiRoot) -> Option<crate::layout_persistence::WindowState> {
        Some(crate::layout_persistence::WindowState {
            maximized: self.maximized,
            persisted_top: self.maximized.then_some(self.normal_top),
            persisted_height: self.maximized.then_some(self.normal_height),
            ..Default::default()
        })
    }

    fn restore_window_state(
        &mut self,
        ui: &mut UiRoot,
        state: &crate::layout_persistence::WindowState,
    ) -> bool {
        // OpenAC `ChatWindowController.RestoreWindowState`.
        if (state.persisted_top.is_some() || state.persisted_height.is_some())
            && let Some((name, frame)) = ui.window_of(self.root).map(|w| (w.name.clone(), w.frame))
            && let Some((left, top, width, height)) = ui
                .element(frame)
                .map(|e| (e.left, e.top, e.width, e.height))
        {
            self.maximized = false;
            ui.resize_window(&name, width, state.persisted_height.unwrap_or(height));
            ui.move_window(&name, left, state.persisted_top.unwrap_or(top));
            if let Some(e) = ui.element(frame) {
                self.normal_top = e.top;
                self.normal_height = e.height;
            }
        }
        if state.maximized != self.maximized {
            self.toggle_maximize(ui);
        } else if let Some(b) = self.max_min {
            ui.set_retail_state(
                b,
                if self.maximized {
                    states::MAXIMIZED
                } else {
                    states::MINIMIZED
                },
            );
        }
        true
    }

    fn tick(&mut self, ui: &mut UiRoot, _dt: f64) {
        self.refresh_transcript(ui);
        self.update_unread_indicator(ui);
        if let Some(f) = ui.widget_mut::<Field>(self.input) {
            f.stay_focused_after_submit = self.context.stay_in_chat_mode;
        }
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
            // OpenAC binds the field's text-changed hook to the entry's
            // draft and its history to the entry's.
            ElementMessage::TextChanged(text) if element == self.input => {
                entry_command(ui, ChatEntryCommand::SetDraft(text.clone()));
                true
            }
            ElementMessage::Recall(request) if element == self.input => {
                self.send_recall_requests(ui, vec![*request]);
                true
            }
            ElementMessage::MenuOpening if Some(element) == self.menu => {
                self.rebuild_menu(ui);
                true
            }
            ElementMessage::MenuSelected(p) if Some(element) == self.menu => {
                self.on_menu_selected(ui, *p);
                true
            }
            ElementMessage::Clicked { .. } if Some(element) == self.send => {
                self.submit_field(ui);
                true
            }
            ElementMessage::Clicked { .. } if Some(element) == self.unread => {
                self.scroll_to_newest_and_clear_unread(ui);
                true
            }
            ElementMessage::Clicked { .. } if Some(element) == self.max_min => {
                self.toggle_maximize(ui);
                true
            }
            ElementMessage::Clicked { .. } if self.indicator_clicks => {
                match self.indicators.iter().position(|i| *i == Some(element)) {
                    Some(i) => {
                        ui.emit(UiOutput::Panel(PanelOutput::ToggleFloatingChat(
                            i as u8 + 1,
                        )));
                        true
                    }
                    None => false,
                }
            }
            ElementMessage::TextClicked(pos) if element == self.transcript => {
                self.handle_tag_click(ui, *pos)
            }
            _ => false,
        }
    }
}
