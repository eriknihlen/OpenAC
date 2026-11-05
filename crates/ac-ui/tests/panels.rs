//! The chat, vitals and radar panel controllers.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ChatWindowControllerTests.cs`,
//! `ChatTagClickTests.cs`, `VitalsBindingTests.cs`,
//! `VitalsSideBySideControllerTests.cs`, `RadarControllerTests.cs` and
//! `RadarSnapshotProviderTests.cs` (projection cases), on synthetic layouts
//! shaped like the retail ones. OpenAC's command bus is
//! [`PanelOutput`] here; its shared chat entry owner is the session's, which
//! the tests stand in for with the same [`ChatEntryState`].

mod common;

use std::sync::Arc;

/// `SessionCommand` has no equality (it can carry a channel sender):
/// compare what it prints.
macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

use ac_dat::objects::{DIRECT_STATE_ID, FontCharDesc, PropertyValue};
use ac_runtime::communication::{ChatChannelKind, ChatEntryCommand, ChatEntryState};
use ac_runtime::session::SessionCommand;
use ac_runtime::session::interface::ChatEntryView;
use ac_ui::draw::{Color, NoSprites, TextureRef};
use ac_ui::element_info::{ElementInfo, ImageMedia, StateInfo};
use ac_ui::event::{Key, Modifiers};
use ac_ui::font::{Atlas, UiFont};
use ac_ui::host::UiHost;
use ac_ui::import::{BuildContext, ImportedLayout, LayoutSource, build};
use ac_ui::panels::PanelOutput;
use ac_ui::panels::chat::{self, ChatContext, ChatSubmission, ChatWindowController};
use ac_ui::panels::radar::{self, RadarController, RadarObject, RadarPlayer, RadarScene};
use ac_ui::panels::transcript::{TranscriptLine, chat_color};
use ac_ui::panels::vitals::{self, VitalView, VitalsController, VitalsSideBySide, VitalsView};
use ac_ui::radar::{BlipShape, RadarBehavior, RadarSnapshot, RelationshipTraits};
use ac_ui::root::{UiOutput, UiRoot};
use ac_ui::states;
use ac_ui::text::Pos;
use ac_ui::tree::Element;
use ac_ui::widgets::{Button, Field, Menu, Meter, Radar, Scrollbar, Text};
use ac_ui::{ElementId, MouseButton};
use common::*;
use glam::{Quat, Vec3};

#[derive(Debug, Default)]
struct NoSource;

impl LayoutSource for NoSource {
    fn layout(&self, _id: u32) -> Option<Arc<ac_dat::objects::LayoutDesc>> {
        None
    }
}

fn info(id: u32, kind: u32, x: f32, y: f32, w: f32, h: f32) -> ElementInfo {
    ElementInfo {
        id,
        kind,
        x,
        y,
        width: w,
        height: h,
        ..ElementInfo::default()
    }
}

fn with(mut parent: ElementInfo, children: Vec<ElementInfo>) -> ElementInfo {
    parent.children = children;
    parent
}

fn editable(mut i: ElementInfo) -> ElementInfo {
    let mut s = StateInfo::named(DIRECT_STATE_ID, "");
    s.properties.set_value(0x16, PropertyValue::Bool(true));
    s.properties.set_value(0x27, PropertyValue::Bool(true));
    s.properties.set_value(0x20, PropertyValue::Bool(true));
    i.set_state(DIRECT_STATE_ID, s);
    i
}

fn build_layout(ui: &mut UiRoot, info: &ElementInfo) -> ImportedLayout {
    let ctx = BuildContext {
        source: &NoSource,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    };
    build(ui, info, &ctx)
}

/// A 6-pixel monospace font, 10 pixels high.
fn font() -> Arc<UiFont> {
    let glyphs = (0x20u16..0x7F).map(|c| FontCharDesc {
        unicode: c,
        offset_x: 0,
        offset_y: 0,
        width: 6,
        height: 10,
        horizontal_offset_before: 0,
        horizontal_offset_after: 0,
        vertical_offset_before: 0,
    });
    Arc::new(UiFont::new(
        0x4000_0001,
        Atlas {
            texture: TextureRef::Surface(0x0600_0001),
            width: 256,
            height: 256,
        },
        None,
        10.0,
        8.0,
        glyphs,
        0,
        0,
    ))
}

// ── Chat ────────────────────────────────────────────────────────────────

fn chat_info() -> ElementInfo {
    let button = |id, x| info(id, 1, x, 0.0, 16.0, 16.0);
    with(
        info(chat::ROOT_ID, 3, 0.0, 0.0, 410.0, 100.0),
        vec![
            with(
                info(chat::TRANSCRIPT_PANEL_ID, 3, 5.0, 5.0, 400.0, 70.0),
                vec![
                    info(chat::TRANSCRIPT_ID, 12, 0.0, 0.0, 384.0, 30.0),
                    info(chat::TRACK_ID, 11, 384.0, 0.0, 16.0, 70.0),
                    info(chat::UNREAD_INDICATOR_ID, 1, 360.0, 50.0, 16.0, 16.0),
                ],
            ),
            with(
                info(chat::INPUT_BAR_ID, 3, 5.0, 78.0, 400.0, 17.0),
                vec![
                    with(
                        info(chat::MENU_ID, 6, 0.0, 0.0, 46.0, 17.0),
                        vec![info(0x1000_0015, 12, 0.0, 0.0, 46.0, 17.0)],
                    ),
                    editable(info(chat::INPUT_ID, 12, 50.0, 0.0, 300.0, 17.0)),
                    info(chat::SEND_ID, 1, 355.0, 0.0, 45.0, 17.0),
                ],
            ),
            button(chat::INDICATOR_IDS[0], 200.0),
            button(chat::INDICATOR_IDS[1], 220.0),
            button(chat::INDICATOR_IDS[2], 240.0),
            button(chat::INDICATOR_IDS[3], 260.0),
            button(chat::MAX_MIN_ID, 390.0),
        ],
    )
}

/// A host with the chat window mounted at (`left`, `top`) and registered.
fn chat_host(left: f32, top: f32) -> (UiHost, ElementId) {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let layout = build_layout(&mut host.root, &chat_info());
    let r = host.root.root();
    host.root.add_child(r, layout.root);
    if let Some(e) = host.root.element_mut(layout.root) {
        e.left = left;
        e.top = top;
        e.min_height = 100.0;
        e.max_height = 2000.0;
    }
    host.root
        .register_window("chat", layout.root, None, 0)
        .unwrap();
    let c = ChatWindowController::bind(&mut host.root, &layout, Some(font()), None).unwrap();
    host.attach_controller("chat", Box::new(c)).unwrap();
    (host, layout.root)
}

fn with_chat<R>(
    host: &mut UiHost,
    f: impl FnOnce(&mut ChatWindowController, &mut UiRoot) -> R,
) -> R {
    host.with_controller::<ChatWindowController, _>("chat", f)
        .unwrap()
}

fn session_commands(host: &mut UiHost) -> Vec<SessionCommand> {
    host.take_outputs()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Panel(PanelOutput::Session(c)) => Some(c),
            _ => None,
        })
        .collect()
}

/// The requests to the session's chat entry among the outputs.
fn entry_commands(host: &mut UiHost) -> Vec<ChatEntryCommand> {
    session_commands(host)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::ChatEntry(e) => Some(e),
            _ => None,
        })
        .collect()
}

/// The lines sent to the session's chat entry among the outputs.
fn sent_lines(host: &mut UiHost) -> Vec<String> {
    entry_commands(host)
        .into_iter()
        .filter_map(|c| match c {
            ChatEntryCommand::Submit(Some(text)) => Some(text),
            _ => None,
        })
        .collect()
}

/// The session's chat entry, as the session keeps it, fed the window's
/// requests: what it then tells the window (OpenAC's window shares
/// `RuntimeChatEntryOwner` directly; here the requests and the view travel
/// between them).
#[derive(Default)]
struct SessionEntry {
    state: ChatEntryState,
    sent: Vec<(String, ChatChannelKind, Option<String>)>,
}

impl SessionEntry {
    fn run(&mut self, host: &mut UiHost) {
        for command in entry_commands(host) {
            let e = &mut self.state;
            match command {
                ChatEntryCommand::Submit(text) => {
                    let sent = &mut self.sent;
                    e.submit(text.as_deref(), |line, d| {
                        sent.push((line.to_owned(), d.channel, d.tell_target.map(str::to_owned)));
                        ac_runtime::communication::SubmitOutcome::Empty
                    });
                }
                ChatEntryCommand::SetDraft(t) => e.type_draft(&t),
                ChatEntryCommand::Compose(t) => {
                    e.compose(&t);
                }
                ChatEntryCommand::SetChannel(c) => e.set_channel(c),
                ChatEntryCommand::SetTellTarget { name, guid } => e.set_tell_target(&name, guid),
                ChatEntryCommand::RecallPrevious => {
                    e.recall_previous();
                }
                ChatEntryCommand::RecallNext => {
                    e.recall_next();
                }
                ChatEntryCommand::ResetRecall => e.reset_recall(),
                ChatEntryCommand::Clear => e.clear(),
            }
        }
        let view = self.view();
        with_chat(host, |c, ui| c.set_entry(ui, &view));
    }

    fn view(&self) -> ChatEntryView {
        let e = &self.state;
        ChatEntryView {
            draft: e.draft().to_owned(),
            shown_revision: e.shown_revision(),
            composed: e.is_input_active(),
            channel: e.active_channel(),
            tell_target: e.tell_target().map(str::to_owned),
            tell_target_guid: e.tell_target_guid(),
            ..ChatEntryView::default()
        }
    }
}

/// The command a line typed on a talk focus becomes (a floating window's
/// entry, which keeps its own talk focus).
fn typed(text: &str, channel: ChatChannelKind, target: Option<(&str, u32)>) -> SessionCommand {
    SessionCommand::ChatInput {
        text: text.into(),
        channel,
        tell_target: target.map(|(name, _)| name.to_owned()),
        tell_target_guid: target.map_or(0, |(_, guid)| guid),
    }
}

fn type_line(host: &mut UiHost, text: &str) {
    with_chat(host, |c, ui| c.enter_chat_mode(ui, None));
    for ch in text.chars() {
        host.char_typed(ch);
    }
    host.key_down(Key::Enter, Modifiers::NONE);
}

fn input(host: &mut UiHost) -> ElementId {
    with_chat(host, |c, _| c.input())
}

fn field_text(host: &mut UiHost) -> String {
    let i = input(host);
    host.root.widget::<Field>(i).unwrap().text()
}

#[test]
fn bind_needs_the_transcript_panel_entry_bar_and_field() {
    let mut ui = root(800.0, 600.0);
    let layout = build_layout(&mut ui, &chat_info());
    assert!(ChatWindowController::bind(&mut ui, &layout, None, None).is_some());
    let mut broken = chat_info();
    broken.children.remove(0);
    let mut ui = root(800.0, 600.0);
    let layout = build_layout(&mut ui, &broken);
    assert!(ChatWindowController::bind(&mut ui, &layout, None, None).is_none());
}

#[test]
fn bind_shapes_the_transcript_input_and_scrollbar() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let (t, i, s) = with_chat(&mut host, |c, _| {
        (c.transcript(), c.input(), c.scrollbar().unwrap())
    });
    let text = host.root.widget::<Text>(t).unwrap();
    assert!(text.selectable() && !text.one_line && !text.centered);
    assert!(host.root.widget::<Field>(i).is_some());
    let parent = host.root.parent(t).unwrap();
    assert_eq!(
        host.root.element(parent).unwrap().dat_element_id,
        chat::TRANSCRIPT_PANEL_ID
    );
    assert_eq!(
        host.root.widget::<Scrollbar>(s).unwrap().model,
        ac_ui::widgets::ScrollSource::Element(t)
    );
}

#[test]
fn a_typed_line_becomes_a_session_chat_command_and_leaves_the_entry() {
    let (mut host, _) = chat_host(0.0, 400.0);
    host.take_outputs();
    type_line(&mut host, "hello");
    assert_eq!(sent_lines(&mut host), ["hello"]);
    assert_eq!(field_text(&mut host), "");
    assert_eq!(host.root.keyboard_focus(), None);
    // Chat mode keeps the entry focused.
    with_chat(&mut host, |c, _| {
        c.set_context(ChatContext {
            stay_in_chat_mode: true,
            ..ChatContext::default()
        });
    });
    host.tick(0.016, 16, None);
    type_line(&mut host, "again");
    let i = input(&mut host);
    assert_eq!(host.root.keyboard_focus(), Some(i));
}

#[test]
fn the_send_button_submits_the_entry() {
    let (mut host, root_el) = chat_host(0.0, 400.0);
    with_chat(&mut host, |c, ui| c.enter_chat_mode(ui, None));
    for ch in "ok".chars() {
        host.char_typed(ch);
    }
    host.take_outputs();
    let (sx, sy) = host.root.screen_position(root_el);
    let (x, y) = (sx as i32 + 5 + 360, sy as i32 + 78 + 5);
    host.mouse_down(MouseButton::Left, x, y, 0);
    host.mouse_up(MouseButton::Left, x, y, 0);
    assert_eq!(
        entry_commands(&mut host),
        [
            ChatEntryCommand::Submit(Some("ok".into())),
            ChatEntryCommand::SetDraft(String::new()),
        ]
    );
}

// OpenAC `ChatWindowControllerTests.TypingInThePanelIsTheSharedEntrysDraft`.
#[test]
fn typing_in_the_panel_is_the_shared_entrys_draft() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut session = SessionEntry::default();
    with_chat(&mut host, |c, ui| c.enter_chat_mode(ui, None));
    for ch in "hello".chars() {
        host.char_typed(ch);
    }
    session.run(&mut host);
    assert_eq!(session.state.draft(), "hello");
    assert_eq!(field_text(&mut host), "hello");
}

// `ALineStagedInTheSharedEntryAppearsInThePanelsField`: a plugin's staged
// line shows in the field, which takes the keyboard for it.
#[test]
fn a_line_staged_in_the_shared_entry_appears_in_the_panels_field() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut session = SessionEntry::default();
    assert!(session.state.compose("/vt nav"));
    session.run(&mut host);
    assert_eq!(field_text(&mut host), "/vt nav");
    let i = input(&mut host);
    assert_eq!(host.root.keyboard_focus(), Some(i));
}

// `SendingFromThePanelRoutesThroughTheSharedEntrysChannel`.
#[test]
fn sending_from_the_panel_routes_through_the_shared_entrys_channel() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut session = SessionEntry::default();
    session.state.set_channel(ChatChannelKind::Fellowship);
    session.run(&mut host);
    assert_eq!(
        with_chat(&mut host, |c, _| c.channel()),
        ChatChannelKind::Fellowship
    );
    type_line(&mut host, "group up");
    session.run(&mut host);
    assert_eq!(
        session.sent,
        [("group up".to_owned(), ChatChannelKind::Fellowship, None)]
    );
    assert_eq!(field_text(&mut host), "");
}

// `ThePanelRecallsTheLinesTheSharedEntryRemembers`: the arrow keys walk the
// lines the session's entry remembers, whichever front end sent them.
#[test]
fn the_panel_recalls_the_lines_the_shared_entry_remembers() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut session = SessionEntry::default();
    session.state.remember("first".into());
    session.state.remember("second".into());
    session.run(&mut host);
    with_chat(&mut host, |c, ui| c.enter_chat_mode(ui, None));
    host.key_down(Key::Up, Modifiers::NONE);
    session.run(&mut host);
    assert_eq!(field_text(&mut host), "second");
    host.key_down(Key::Up, Modifiers::NONE);
    session.run(&mut host);
    assert_eq!(field_text(&mut host), "first");
    host.key_down(Key::Down, Modifiers::NONE);
    session.run(&mut host);
    assert_eq!(field_text(&mut host), "second");
    // Typing ends the walk: Up starts again at the newest.
    host.char_typed('!');
    session.run(&mut host);
    assert!(!session.state.is_recalling());
    assert_eq!(session.state.draft(), "second!");
}

// `ALineSentFromThePanelIsRememberedOnceByTheSharedEntry`.
#[test]
fn a_line_sent_from_the_panel_is_remembered_once_by_the_shared_entry() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut session = SessionEntry::default();
    type_line(&mut host, "hello");
    session.run(&mut host);
    assert_eq!(session.state.count(), 1);
    assert_eq!(session.state.recall_previous().as_deref(), Some("hello"));
    let i = input(&mut host);
    assert_eq!(host.root.widget::<Field>(i).unwrap().history_count(), 0);
}

fn pick_channel(host: &mut UiHost, kind: ChatChannelKind) {
    let payload = chat::CHANNEL_ITEMS
        .iter()
        .position(|(_, _, c)| *c == kind)
        .unwrap() as u64;
    with_chat(host, |c, ui| {
        let m = c.menu().unwrap();
        ac_ui::host::PanelController::on_element_message(
            c,
            ui,
            m,
            &ac_ui::root::ElementMessage::MenuSelected(payload),
        );
    });
}

#[test]
fn a_channel_pick_routes_plain_lines_and_keeps_commands_as_typed() {
    // OpenAC `Bind_ChannelChange_UpdatesSubmitChannel`: the pick aims the
    // shared entry, whose talk focus the lines then go with.
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut session = SessionEntry::default();
    pick_channel(&mut host, ChatChannelKind::General);
    assert_eq!(
        with_chat(&mut host, |c, _| c.channel()),
        ChatChannelKind::General
    );
    let m = with_chat(&mut host, |c, _| c.menu().unwrap());
    assert_eq!(host.root.widget::<Menu>(m).unwrap().button_label, "Gen");
    session.run(&mut host);
    assert_eq!(session.state.active_channel(), ChatChannelKind::General);
    type_line(&mut host, "anyone?");
    type_line(&mut host, "/e waves");
    session.run(&mut host);
    assert_eq!(
        session.sent,
        [
            ("anyone?".to_owned(), ChatChannelKind::General, None),
            ("/e waves".to_owned(), ChatChannelKind::General, None),
        ]
    );
}

#[test]
fn the_talk_button_uses_the_authored_short_captions() {
    let (mut host, _) = chat_host(0.0, 400.0);
    for (kind, caption) in [
        (ChatChannelKind::Say, "Chat"),
        (ChatChannelKind::General, "Gen"),
        (ChatChannelKind::Trade, "Trade"),
        (ChatChannelKind::Lfg, "LFG"),
    ] {
        pick_channel(&mut host, kind);
        assert_eq!(
            with_chat(&mut host, |c, _| c.channel_button_label()),
            caption
        );
    }
}

#[test]
fn the_talk_focus_specials_carry_the_selected_name_and_act_on_it() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let m = with_chat(&mut host, |c, ui| {
        c.set_context(ChatContext {
            selected_target: Some(("+Acdream".into(), 0x5000_0001)),
            ..ChatContext::default()
        });
        let m = c.menu().unwrap();
        ac_ui::host::PanelController::on_element_message(
            c,
            ui,
            m,
            &ac_ui::root::ElementMessage::MenuOpening,
        );
        m
    });
    let items = host.root.widget::<Menu>(m).unwrap().items.clone();
    assert_eq!(items[0].label, "Squelch (ignore) +Acdream");
    assert_eq!(items[1].label, "Tell to +Acdream");
    assert!(items[0].enabled && items[1].enabled);
    with_chat(&mut host, |c, ui| {
        ac_ui::host::PanelController::on_element_message(
            c,
            ui,
            m,
            &ac_ui::root::ElementMessage::MenuSelected(chat::TELL_TO_SELECTED_PAYLOAD),
        );
        assert_eq!(c.channel(), ChatChannelKind::Tell);
        assert_eq!(c.tell_target(), Some(&("+Acdream".to_owned(), 0x5000_0001)));
    });
    let mut session = SessionEntry::default();
    type_line(&mut host, "yo");
    session.run(&mut host);
    // OpenAC `TellToSelected_CarriesThePickedObjectId_Issue50`.
    assert_eq!(session.state.tell_target_guid(), 0x5000_0001);
    assert_eq!(
        session.sent,
        [(
            "yo".to_owned(),
            ChatChannelKind::Tell,
            Some("+Acdream".to_owned())
        )]
    );
    with_chat(&mut host, |c, ui| {
        ac_ui::host::PanelController::on_element_message(
            c,
            ui,
            m,
            &ac_ui::root::ElementMessage::MenuSelected(chat::SQUELCH_PAYLOAD),
        );
    });
    assert_dbg_eq!(
        session_commands(&mut host),
        vec![SessionCommand::Squelch {
            add: true,
            name: "+Acdream".into(),
            account: false
        }]
    );
    // Picking a channel drops the picked object.
    pick_channel(&mut host, ChatChannelKind::Say);
    assert_eq!(with_chat(&mut host, |c, _| c.tell_target().cloned()), None);
}

#[test]
fn the_talk_focus_specials_are_inert_and_unnamed_with_nothing_selected() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let m = with_chat(&mut host, |c, _| c.menu().unwrap());
    let items = host.root.widget::<Menu>(m).unwrap().items.clone();
    assert_eq!(items[0].label, "Squelch (ignore) Selected");
    assert_eq!(items[1].label, "Tell to Selected");
    assert!(!items[0].enabled && !items[1].enabled);
    assert_eq!(items.len(), 14);
    let enabled: Vec<&str> = items[2..]
        .iter()
        .filter(|i| i.enabled)
        .map(|i| i.label.as_str())
        .collect();
    assert_eq!(
        enabled,
        [
            "Chat to All",
            "Tell to General Chat",
            "Tell to LFG Chat",
            "Tell to Trade Chat"
        ]
    );
}

#[test]
fn start_tell_prefills_the_entry_and_puts_the_caret_at_the_end() {
    let (mut host, _) = chat_host(0.0, 400.0);
    with_chat(&mut host, |c, ui| c.start_tell(ui, "Dww"));
    let i = input(&mut host);
    let f = host.root.widget::<Field>(i).unwrap();
    assert_eq!(f.text(), "@tell Dww, ");
    assert_eq!(f.caret_pos(), "@tell Dww, ".len());
    assert_eq!(host.root.keyboard_focus(), Some(i));
    // The shared entry's draft follows (OpenAC: `SetText` raises the
    // field's text-changed hook).
    assert_eq!(
        entry_commands(&mut host),
        [ChatEntryCommand::SetDraft("@tell Dww, ".into())]
    );
}

/// The reply targets as the session reports them.
fn reply_targets(host: &mut UiHost, tell: Option<&str>, monarch: Option<&str>) {
    let view = ChatEntryView {
        last_incoming_tell_sender: tell.map(str::to_owned),
        last_monarch_sender: monarch.map(str::to_owned),
        ..ChatEntryView::default()
    };
    with_chat(host, |c, ui| c.set_entry(ui, &view));
}

#[test]
fn typing_slash_r_space_expands_to_a_tell_to_the_last_teller() {
    // OpenAC `ChatWindowController.Bind`: the text replacer reads the chat
    // view's `LastIncomingTellSender`, which the session reports here.
    let (mut host, _) = chat_host(0.0, 400.0);
    reply_targets(&mut host, Some("Dww"), None);
    with_chat(&mut host, |c, ui| c.enter_chat_mode(ui, None));
    for ch in "/r ".chars() {
        host.char_typed(ch);
    }
    assert_eq!(field_text(&mut host), "@tell Dww, ");
}

// OpenAC `RetailUiRuntime.HandleInputAction`: ChatReply starts a tell to
// the last teller, ChatMonarchReply to the monarch's last speaker; with
// nobody to answer, nothing happens.
#[test]
fn the_reply_keys_start_a_tell_to_who_last_spoke() {
    let (mut host, _) = chat_host(0.0, 400.0);
    with_chat(&mut host, |c, ui| {
        let e = c.entry().clone();
        c.start_reply(ui, e.last_incoming_tell_sender.as_deref());
    });
    assert_eq!(field_text(&mut host), "");
    reply_targets(&mut host, Some("Dww"), Some("Monarch Bob"));
    with_chat(&mut host, |c, ui| {
        let e = c.entry().clone();
        c.start_reply(ui, e.last_monarch_sender.as_deref());
    });
    assert_eq!(field_text(&mut host), "@tell Monarch Bob, ");
    with_chat(&mut host, |c, ui| c.start_command(ui));
    assert_eq!(field_text(&mut host), "/");
    let i = input(&mut host);
    assert_eq!(host.root.keyboard_focus(), Some(i));
}

fn push(host: &mut UiHost, seq: u64, ty: u32, text: &str) {
    with_chat(host, |c, _| {
        c.push_line(TranscriptLine::plain(seq, ty, text))
    });
}

fn frame(host: &mut UiHost, ms: i64) {
    host.tick(0.016, ms, None);
    host.draw((800.0, 600.0), &NoSprites);
}

#[test]
fn transcript_lines_take_their_class_colours_and_carry_unknown_classes() {
    let (mut host, _) = chat_host(0.0, 400.0);
    with_chat(&mut host, |c, _| {
        let mut filters = ac_core::chat::ChatWindowState::new();
        filters.set_filter(ac_core::chat::ChatWindowId::MAIN, u64::MAX);
        c.set_filters(filters);
    });
    push(&mut host, 1, 0x05, "first");
    push(&mut host, 2, 0x22, "middle");
    push(&mut host, 3, 0x00, "third");
    frame(&mut host, 16);
    let t = with_chat(&mut host, |c, _| c.transcript());
    let lines = host.root.widget::<Text>(t).unwrap().shown_lines().to_vec();
    assert_eq!(lines.len(), 3);
    assert_eq!(lines[0].color, lines[1].color);
    assert_ne!(lines[0].color, lines[2].color);
    assert_eq!(lines[0].color, chat_color(0x05).unwrap());
}

#[test]
fn the_transcript_layout_is_reused_until_content_or_width_changes() {
    let (mut host, root_el) = chat_host(0.0, 400.0);
    push(&mut host, 1, 0x02, "one");
    frame(&mut host, 16);
    let builds = with_chat(&mut host, |c, _| c.transcript_layout_builds);
    frame(&mut host, 32);
    frame(&mut host, 48);
    assert_eq!(
        with_chat(&mut host, |c, _| c.transcript_layout_builds),
        builds
    );
    push(&mut host, 2, 0x02, "two");
    frame(&mut host, 64);
    assert_eq!(
        with_chat(&mut host, |c, _| c.transcript_layout_builds),
        builds + 1
    );
    let t = with_chat(&mut host, |c, _| c.transcript());
    let _ = root_el;
    host.root.element_mut(t).unwrap().width = 200.0;
    frame(&mut host, 80);
    assert_eq!(
        with_chat(&mut host, |c, _| c.transcript_layout_builds),
        builds + 2
    );
}

#[test]
fn new_text_while_scrolled_up_lights_the_indicator_until_the_bottom() {
    let (mut host, _) = chat_host(0.0, 400.0);
    for i in 0..10 {
        push(&mut host, i, 0x02, &format!("line {i}"));
    }
    frame(&mut host, 16);
    frame(&mut host, 32);
    let (t, u) = with_chat(&mut host, |c, _| {
        (c.transcript(), c.unread_indicator().unwrap())
    });
    assert!(!host.root.element(u).unwrap().visible());
    host.root.scroll_model_mut(t).unwrap().set_scroll_y(0);
    push(&mut host, 10, 0x02, "line 10");
    frame(&mut host, 48);
    assert!(with_chat(&mut host, |c, _| c.has_unseen_text()));
    assert!(host.root.element(u).unwrap().visible());
    // Clicking the indicator jumps to the newest text.
    with_chat(&mut host, |c, ui| {
        ac_ui::host::PanelController::on_element_message(
            c,
            ui,
            u,
            &ac_ui::root::ElementMessage::Clicked { x: 1, y: 1 },
        );
    });
    assert!(!with_chat(&mut host, |c, _| c.has_unseen_text()));
    frame(&mut host, 64);
    assert!(!host.root.element(u).unwrap().visible());
    assert!(host.root.scroll_model(t).unwrap().at_end());
}

#[test]
fn reaching_the_bottom_clears_the_unseen_flag_however_you_got_there() {
    let (mut host, _) = chat_host(0.0, 400.0);
    for i in 0..10 {
        push(&mut host, i, 0x02, &format!("line {i}"));
    }
    frame(&mut host, 16);
    with_chat(&mut host, |c, _| c.set_unread(true));
    let t = with_chat(&mut host, |c, _| c.transcript());
    host.root.scroll_model_mut(t).unwrap().scroll_to_end();
    host.tick(0.016, 32, None);
    assert!(!with_chat(&mut host, |c, _| c.has_unseen_text()));
}

#[test]
fn a_tagged_name_click_starts_a_tell_and_a_coordinate_reports_the_place() {
    let (mut host, _) = chat_host(0.0, 400.0);
    let line = ac_runtime::communication::ChatLine {
        sequence: 1,
        kind: ac_core::chat::ChatKind::Tell,
        log_text_type: 0x03,
        combat_kind: None,
        received_unix_ms: 0,
        text: "Dww tells you, \"hi\"".into(),
        channel_name: String::new(),
        sender: "Dww".into(),
        sender_guid: 0x5000_000A,
        words: "hi".into(),
        display_text: String::new(),
        channel_id: 0,
        // The session splits a line naming another player at the name.
        spans: Some(ac_core::chat::tag_markup::parse(
            "<Tell:IIDString:1342177290:Dww>Dww<\\Tell> tells you, \"hi\"",
        )),
    };
    with_chat(&mut host, |c, _| c.push_chat_line(&line));
    frame(&mut host, 16);
    let (text, ranges) = with_chat(&mut host, |c, _| {
        (
            c.transcript_layout().lines[0].text.clone(),
            c.tags_of_line(0).map(<[_]>::to_vec),
        )
    });
    assert_eq!(text, "Dww tells you, \"hi\"");
    let ranges = ranges.unwrap();
    assert_eq!((ranges[0].0, ranges[0].1), (0, 3));
    assert!(!with_chat(&mut host, |c, ui| c.handle_tag_click(ui, Pos::new(0, 5))));
    assert!(with_chat(&mut host, |c, ui| c.handle_tag_click(ui, Pos::new(0, 1))));
    assert_eq!(field_text(&mut host), "@tell Dww, ");

    with_chat(&mut host, |c, _| {
        c.push_line(TranscriptLine {
            // A coordinate link comes from a plugin's line, read as one.
            spans: Some(ac_core::chat::tag_markup::parse_links(
                "Meet at <COORD:World:12.3N, 45.6W>12.3N, 45.6W<\\COORD>",
            )),
            ..TranscriptLine::plain(2, 0x05, "Meet at 12.3N, 45.6W")
        });
    });
    frame(&mut host, 32);
    host.take_outputs();
    assert!(with_chat(&mut host, |c, ui| c.handle_tag_click(ui, Pos::new(1, 9))));
    let got = host.take_outputs().into_iter().find_map(|o| match o {
        UiOutput::Panel(PanelOutput::CoordinateLinkClicked {
            east_west,
            north_south,
            text,
        }) => Some((east_west, north_south, text)),
        _ => None,
    });
    assert_eq!(got, Some((-45.6, 12.3, "12.3N, 45.6W".to_owned())));
}

#[test]
fn indicators_mirror_the_floating_windows_and_clicks_toggle_them_when_bound() {
    let (mut host, root_el) = chat_host(0.0, 400.0);
    let ids: Vec<ElementId> = chat::INDICATOR_IDS
        .iter()
        .map(|id| host.root.find_descendant(root_el, *id).unwrap())
        .collect();
    assert!(
        host.root
            .widget::<Button>(ids[0])
            .unwrap()
            .suppress_self_toggle
    );
    host.take_outputs();
    let (sx, sy) = host.root.screen_position(ids[1]);
    host.mouse_down(MouseButton::Left, sx as i32 + 2, sy as i32 + 2, 0);
    host.mouse_up(MouseButton::Left, sx as i32 + 2, sy as i32 + 2, 0);
    assert!(
        !host
            .take_outputs()
            .iter()
            .any(|o| matches!(o, UiOutput::Panel(PanelOutput::ToggleFloatingChat(_))))
    );
    with_chat(&mut host, |c, ui| c.bind_indicator_clicks(ui));
    host.mouse_down(MouseButton::Left, sx as i32 + 2, sy as i32 + 2, 0);
    host.mouse_up(MouseButton::Left, sx as i32 + 2, sy as i32 + 2, 0);
    assert!(
        host.take_outputs()
            .iter()
            .any(|o| matches!(o, UiOutput::Panel(PanelOutput::ToggleFloatingChat(2))))
    );
}

#[test]
#[should_panic(expected = "outside 1..=4")]
fn an_indicator_out_of_range_is_a_caller_bug() {
    let (mut host, _) = chat_host(0.0, 400.0);
    with_chat(&mut host, |c, ui| c.set_indicator_open(ui, 5, true));
}

#[test]
fn maximize_grows_by_half_the_screen_and_restores() {
    let (mut host, frame_el) = chat_host(0.0, 500.0);
    with_chat(&mut host, |c, ui| c.toggle_maximize(ui));
    let e = host.root.element(frame_el).unwrap();
    assert_eq!((e.top, e.height), (200.0, 400.0));
    assert!(with_chat(&mut host, |c, _| c.is_maximized()));
    with_chat(&mut host, |c, ui| c.toggle_maximize(ui));
    let e = host.root.element(frame_el).unwrap();
    assert_eq!((e.top, e.height), (500.0, 100.0));
    // Room below: it grows downward.
    let (mut host, frame_el) = chat_host(0.0, 0.0);
    with_chat(&mut host, |c, ui| c.toggle_maximize(ui));
    let e = host.root.element(frame_el).unwrap();
    assert_eq!((e.top, e.height), (0.0, 400.0));
}

#[test]
fn submissions_map_to_session_commands() {
    let s = |text: &str, channel, target: Option<&str>| ChatSubmission {
        text: text.into(),
        channel,
        tell_target: target.map(str::to_owned),
        tell_target_guid: 0,
    };
    assert!(
        s("  ", ChatChannelKind::Say, None)
            .to_session_command()
            .is_none()
    );
    // The line goes as typed with the talk focus as the router's defaults
    // (OpenAC `ChatWindowController` hands `SendChatCmd` its channel and
    // target); the runtime router tells verbs from plain lines.
    assert_dbg_eq!(
        s("hi", ChatChannelKind::Say, None).to_session_command(),
        Some(typed("hi", ChatChannelKind::Say, None))
    );
    assert_dbg_eq!(
        s(":waves", ChatChannelKind::Trade, None).to_session_command(),
        Some(typed(":waves", ChatChannelKind::Trade, None))
    );
    assert_dbg_eq!(
        s("wts sword", ChatChannelKind::Trade, None).to_session_command(),
        Some(typed("wts sword", ChatChannelKind::Trade, None))
    );
    assert_dbg_eq!(
        s("yo", ChatChannelKind::Tell, Some("Dww")).to_session_command(),
        Some(typed("yo", ChatChannelKind::Tell, Some(("Dww", 0))))
    );
}

// ── Vitals ──────────────────────────────────────────────────────────────

fn vitals_layout(ui: &mut UiRoot, with_all: bool) -> ImportedLayout {
    let mut children = vec![
        info(vitals::HEALTH, 7, 5.0, 5.0, 150.0, 16.0),
        info(vitals::HEALTH_TEXT, 12, 5.0, 5.0, 150.0, 16.0),
    ];
    if with_all {
        children.extend([
            info(vitals::STAMINA, 7, 5.0, 25.0, 150.0, 16.0),
            info(vitals::STAMINA_TEXT, 12, 5.0, 25.0, 150.0, 16.0),
            info(vitals::MANA, 7, 5.0, 45.0, 150.0, 16.0),
            info(vitals::MANA_TEXT, 12, 5.0, 45.0, 150.0, 16.0),
        ]);
    }
    let layout = build_layout(
        ui,
        &with(info(0x1000_05F9, 3, 0.0, 0.0, 160.0, 70.0), children),
    );
    let r = ui.root();
    ui.add_child(r, layout.root);
    layout
}

fn vital(pct: f32, c: u32, m: u32) -> VitalView {
    VitalView {
        percent: Some(pct),
        current: Some(c),
        max: Some(m),
    }
}

fn number(ui: &UiRoot, layout: &ImportedLayout, id: u32) -> String {
    let t = ui.widget::<Text>(layout.find(id).unwrap()).unwrap();
    assert!(t.centered && t.one_line && !t.selectable());
    t.content_lines()
        .first()
        .map(|l| l.text.clone())
        .unwrap_or_default()
}

#[test]
fn each_meter_shows_its_own_vital() {
    let mut ui = root(800.0, 600.0);
    let layout = vitals_layout(&mut ui, true);
    let mut c = VitalsController::bind(&mut ui, &layout);
    c.apply(
        &mut ui,
        &VitalsView {
            health: vital(0.25, 25, 100),
            stamina: vital(0.5, 50, 100),
            mana: vital(0.75, 75, 100),
        },
    );
    for (bar, text, pct, s) in [
        (vitals::HEALTH, vitals::HEALTH_TEXT, 0.25, "25/100"),
        (vitals::STAMINA, vitals::STAMINA_TEXT, 0.5, "50/100"),
        (vitals::MANA, vitals::MANA_TEXT, 0.75, "75/100"),
    ] {
        let m = ui.widget::<Meter>(layout.find(bar).unwrap()).unwrap();
        assert_eq!(m.fill, Some(pct));
        assert_eq!(m.label, None);
        assert_eq!(number(&ui, &layout, text), s);
    }
}

#[test]
fn missing_meters_are_skipped() {
    let mut ui = root(800.0, 600.0);
    let layout = vitals_layout(&mut ui, false);
    let mut c = VitalsController::bind(&mut ui, &layout);
    c.apply(
        &mut ui,
        &VitalsView {
            health: vital(1.0, 100, 100),
            ..VitalsView::default()
        },
    );
    let m = ui
        .widget::<Meter>(layout.find(vitals::HEALTH).unwrap())
        .unwrap();
    assert_eq!(m.fill, Some(1.0));
    assert_eq!(VitalView::default().text(), "");
    assert_eq!(VitalView::default().fill(), 0.0);
}

#[test]
fn side_by_side_shows_one_window_and_hides_the_other() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let a = ui.add(r, Element::at(0.0, 0.0, 10.0, 10.0), ac_ui::tree::Container);
    let b = ui.add(r, Element::at(0.0, 0.0, 10.0, 10.0), ac_ui::tree::Container);
    ui.register_window("vitals", a, None, 0).unwrap();
    ui.register_window("side-vitals", b, None, 0).unwrap();
    let mut s = VitalsSideBySide::new("vitals", "side-vitals");
    assert_eq!(s.applied(), None);
    s.tick(&mut ui, true);
    assert!(!ui.is_window_visible("vitals") && ui.is_window_visible("side-vitals"));
    s.tick(&mut ui, false);
    assert!(ui.is_window_visible("vitals") && !ui.is_window_visible("side-vitals"));
    assert_eq!(s.applied(), Some(false));
}

/// OpenAC `VitalsSideBySideControllerTests`: `FirstTick_AppliesTheCurrentBit_BothDirections`
/// and `SteadyBit_DoesNotReassertVisibility` (the side row starts hidden;
/// a window hidden elsewhere stays hidden while the bit does not change).
#[test]
fn side_by_side_first_tick_applies_the_bit_and_a_steady_bit_does_not_reassert() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let a = ui.add(
        r,
        Element::at(0.0, 0.0, 160.0, 58.0),
        ac_ui::tree::Container,
    );
    let b = ui.add(
        r,
        Element::at(0.0, 0.0, 460.0, 26.0),
        ac_ui::tree::Container,
    );
    ui.set_visible(b, false);
    ui.register_window("vitals", a, None, 0).unwrap();
    ui.register_window("side-vitals", b, None, 0).unwrap();
    let mut s = VitalsSideBySide::new("vitals", "side-vitals");
    s.tick(&mut ui, false);
    assert!(ui.is_window_visible("vitals") && !ui.is_window_visible("side-vitals"));
    assert_eq!(s.applied(), Some(false));
    ui.hide_window("vitals");
    s.tick(&mut ui, false);
    assert!(!ui.is_window_visible("vitals"));
}

// ── Radar ───────────────────────────────────────────────────────────────

fn sprite(id: u32, kind: u32, x: f32, y: f32, w: f32, h: f32, file: u32) -> ElementInfo {
    let mut i = info(id, kind, x, y, w, h);
    i.state_media
        .insert(String::new(), ImageMedia { file, draw_mode: 1 });
    i
}

fn radar_layout(ui: &mut UiRoot) -> ImportedLayout {
    let mut lock = info(radar::LOCK_BUTTON_ID, 1, 6.0, 6.0, 27.0, 27.0);
    lock.state_media.insert(
        "LockedUI".into(),
        ImageMedia {
            file: 0x0600_74B7,
            draw_mode: 3,
        },
    );
    lock.state_media.insert(
        "UnlockedUI".into(),
        ImageMedia {
            file: 0x0600_74B8,
            draw_mode: 3,
        },
    );
    lock.set_state(
        states::LOCKED_UI,
        StateInfo::named(states::LOCKED_UI, "LockedUI"),
    );
    lock.set_state(
        states::UNLOCKED_UI,
        StateInfo::named(states::UNLOCKED_UI, "UnlockedUI"),
    );
    let root_info = with(
        info(
            radar::ROOT_ID,
            ac_ui::widgets::radar::RETAIL_CLASS_ID,
            0.0,
            0.0,
            120.0,
            140.0,
        ),
        vec![
            sprite(
                radar::COORDINATE_CONTAINER_ID,
                3,
                0.0,
                120.0,
                120.0,
                18.0,
                0x0600_4CC0,
            ),
            sprite(radar::RADAR_DISC_ID, 3, 0.0, 0.0, 120.0, 120.0, 0x0600_4CC1),
            sprite(radar::NORTH_TOKEN_ID, 3, 55.0, 1.0, 10.0, 9.0, 0x0600_11FB),
            sprite(radar::EAST_TOKEN_ID, 3, 110.0, 55.0, 10.0, 9.0, 0x0600_1938),
            sprite(
                radar::SOUTH_TOKEN_ID,
                3,
                55.0,
                110.0,
                10.0,
                9.0,
                0x0600_193A,
            ),
            sprite(radar::WEST_TOKEN_ID, 3, 0.0, 55.0, 10.0, 9.0, 0x0600_193C),
            lock,
            sprite(radar::DRAG_BUTTON_ID, 2, 87.0, 6.0, 27.0, 27.0, 0x0600_74C9),
        ],
    );
    let layout = build_layout(ui, &root_info);
    let r = ui.root();
    ui.add_child(r, layout.root);
    layout
}

fn snap(heading: f32, coords: Option<&str>, locked: bool) -> RadarSnapshot {
    RadarSnapshot {
        player_heading_degrees: heading,
        coordinates_text: coords.map(str::to_owned),
        ui_locked: locked,
        ..RadarSnapshot::default()
    }
}

#[test]
fn bind_uses_the_imported_children_for_compass_coordinates_and_lock() {
    let mut ui = root(800.0, 600.0);
    let layout = radar_layout(&mut ui);
    let mut c = RadarController::bind(&mut ui, &layout, None).unwrap();
    assert_eq!(ui.children(layout.root).len(), 8);
    c.apply_snapshot(&mut ui, snap(0.0, Some("42.1N,33.6E"), false));
    let r = ui.widget::<Radar>(layout.root).unwrap();
    assert_eq!(r.center, (60.0, 60.0));
    assert!(ui.element(layout.root).unwrap().draggable);
    let container = layout.find(radar::COORDINATE_CONTAINER_ID).unwrap();
    assert!(ui.element(container).unwrap().visible());
    let text = c.coordinate_text().unwrap();
    assert_eq!(ui.parent(text), Some(container));
    assert_eq!(
        ui.widget::<Text>(text).unwrap().content_lines()[0].text,
        "42.1N,33.6E"
    );
    let north = ui
        .element(layout.find(radar::NORTH_TOKEN_ID).unwrap())
        .unwrap();
    assert_eq!((north.left, north.top), (55.0, 1.0));
    let lock = layout.find(radar::LOCK_BUTTON_ID).unwrap();
    assert_eq!(
        ui.widget::<Button>(lock).unwrap().active_state(),
        "UnlockedUI"
    );

    c.apply_snapshot(&mut ui, snap(90.0, None, true));
    assert!(!ui.element(layout.root).unwrap().draggable);
    assert!(!ui.element(container).unwrap().visible());
    assert_eq!(
        ui.widget::<Button>(lock).unwrap().active_state(),
        "LockedUI"
    );
    let drag = layout.find(radar::DRAG_BUTTON_ID).unwrap();
    assert!(!ui.element(drag).unwrap().visible());
    assert!(!ui.element(drag).unwrap().click_through);
    let north = ui
        .element(layout.find(radar::NORTH_TOKEN_ID).unwrap())
        .unwrap();
    let expected = ac_ui::radar::compass_token_top_left(
        90.0,
        ac_ui::radar::CompassPoint::North,
        glam::Vec2::new(60.0, 60.0),
        glam::Vec2::new(60.0, 5.5).distance(glam::Vec2::new(60.0, 60.0)),
        glam::Vec2::new(10.0, 9.0),
    );
    assert_eq!((north.left, north.top), (expected.x, expected.y));
}

#[test]
fn the_lock_button_asks_for_the_other_lock_state() {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let layout = radar_layout(&mut host.root);
    host.root
        .register_window("radar", layout.root, None, 0)
        .unwrap();
    let c = RadarController::bind(&mut host.root, &layout, None).unwrap();
    host.attach_controller("radar", Box::new(c)).unwrap();
    host.take_outputs();
    host.mouse_down(MouseButton::Left, 15, 15, 0);
    host.mouse_up(MouseButton::Left, 15, 15, 0);
    assert!(
        host.take_outputs()
            .iter()
            .any(|o| matches!(o, UiOutput::Panel(PanelOutput::SetUiLocked(true))))
    );
}

#[test]
fn a_radar_click_selects_and_hover_reports_the_object() {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let layout = radar_layout(&mut host.root);
    host.root
        .register_window("radar", layout.root, None, 0)
        .unwrap();
    let mut c = RadarController::bind(&mut host.root, &layout, None).unwrap();
    let mut s = snap(0.0, None, false);
    s.blips.push(ac_ui::radar::RadarBlip {
        object_id: 0x20,
        name: "Near".into(),
        pixel_x: 40.0,
        pixel_y: 70.0,
        color: Color::WHITE,
        shape: BlipShape::Plus,
        selected: false,
    });
    c.apply_snapshot(&mut host.root, s);
    host.attach_controller("radar", Box::new(c)).unwrap();
    host.take_outputs();
    host.mouse_move(40, 70);
    host.mouse_down(MouseButton::Left, 40, 70, 0);
    host.mouse_up(MouseButton::Left, 40, 70, 0);
    let outs = host.take_outputs();
    assert!(
        outs.iter()
            .any(|o| matches!(o, UiOutput::Panel(PanelOutput::HoveredObject(Some(0x20)))))
    );
    assert!(
        outs.iter()
            .any(|o| matches!(o, UiOutput::Panel(PanelOutput::SelectObject(0x20))))
    );
}

#[test]
fn frames_are_shown_at_the_retail_25_ms_cadence() {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let layout = radar_layout(&mut host.root);
    host.root
        .register_window("radar", layout.root, None, 0)
        .unwrap();
    let mut c = RadarController::bind(&mut host.root, &layout, None).unwrap();
    c.set_snapshot(RadarSnapshot::default());
    let base = c.frames_applied;
    host.attach_controller("radar", Box::new(c)).unwrap();
    let frames = |h: &mut UiHost| {
        h.with_controller::<RadarController, _>("radar", |c, _| c.frames_applied)
            .unwrap()
            - base
    };
    host.tick(0.024, 24, None);
    assert_eq!(frames(&mut host), 0);
    host.tick(0.0011, 25, None);
    assert_eq!(frames(&mut host), 1);
    host.tick(0.100, 125, None);
    assert_eq!(frames(&mut host), 2);
}

fn player() -> RadarPlayer {
    RadarPlayer {
        guid: 0x5000_0001,
        position: Vec3::new(100.0, 100.0, 10.0),
        rotation: Quat::IDENTITY,
        yaw_radians: std::f32::consts::FRAC_PI_2,
        cell_id: 0xA9B4_0031,
        item_type: 0,
        pwd_bitfield: 0x8,
        monarch_id: 0,
    }
}

fn object(guid: u32, offset: Vec3, behavior: Option<RadarBehavior>) -> RadarObject {
    RadarObject {
        guid,
        name: Some(format!("Obj {guid:X}")),
        position: player().position + offset,
        behavior,
        item_type: 0,
        pwd_bitfield: 0,
        color_override: 0,
        monarch_id: 0,
        relationship: RelationshipTraits::default(),
    }
}

#[test]
fn an_object_sharing_the_players_monarch_is_a_square() {
    // The radar draws a member of the player's allegiance (an object whose
    // monarch is the player's) as a hollow box; one with another monarch,
    // or none, stays a plus; a fellow keeps the fellowship's triangle.
    const MONARCH: u32 = 0x5000_0777;
    let at = |guid, monarch_id| RadarObject {
        monarch_id,
        pwd_bitfield: 0x8,
        ..object(
            guid,
            Vec3::new(0.0, 10.0, 0.0),
            Some(RadarBehavior::ShowAlways),
        )
    };
    let scene = RadarScene {
        player: Some(RadarPlayer {
            monarch_id: MONARCH,
            ..player()
        }),
        objects: vec![
            at(0x20, MONARCH),
            at(0x21, 0x5000_0778),
            at(0x22, 0),
            RadarObject {
                relationship: RelationshipTraits {
                    is_fellowship_member: true,
                    ..RelationshipTraits::default()
                },
                ..at(0x23, MONARCH)
            },
        ],
        ..RadarScene::default()
    };
    let shapes: Vec<_> = radar::build_snapshot(&scene)
        .blips
        .iter()
        .map(|b| (b.object_id, b.shape))
        .collect();
    assert_eq!(
        shapes,
        [
            (0x20, BlipShape::Box),
            (0x21, BlipShape::Plus),
            (0x22, BlipShape::Plus),
            (0x23, BlipShape::InvertedTriangle),
        ]
    );
    // A player in no allegiance: nobody without a monarch is "his".
    let alone = RadarScene {
        player: Some(player()),
        ..scene
    };
    assert!(
        radar::build_snapshot(&alone)
            .blips
            .iter()
            .filter(|b| b.object_id != 0x23)
            .all(|b| b.shape == BlipShape::Plus)
    );
}

#[test]
fn a_scene_projects_into_blips_around_the_player() {
    let scene = RadarScene {
        player: Some(player()),
        objects: vec![
            object(
                0x10,
                Vec3::new(15.0, 30.0, 0.0),
                Some(RadarBehavior::ShowAlways),
            ),
            object(
                0x11,
                Vec3::new(1.0, 1.0, 0.0),
                Some(RadarBehavior::ShowNever),
            ),
            object(0x12, Vec3::new(1.0, 1.0, 0.0), None),
            object(0x5000_0001, Vec3::ZERO, Some(RadarBehavior::ShowAlways)),
            object(
                0x13,
                Vec3::new(80.0, 0.0, 0.0),
                Some(RadarBehavior::ShowAlways),
            ),
            RadarObject {
                name: None,
                // Hidden from the UI: no blip.
                pwd_bitfield: 0x0000_0080,
                ..object(
                    0x14,
                    Vec3::new(1.0, 0.0, 0.0),
                    Some(RadarBehavior::ShowAlways),
                )
            },
            RadarObject {
                name: None,
                ..object(
                    0xABCD,
                    Vec3::new(0.0, -15.0, 6.0),
                    Some(RadarBehavior::ShowMovement),
                )
            },
        ],
        selected: Some(0x10),
        coordinates_on_radar: true,
        ui_locked: false,
    };
    let s = radar::build_snapshot(&scene);
    assert_eq!(s.player_heading_degrees, 0.0);
    assert_eq!(s.coordinates_text.as_deref(), Some("42.1N,33.9E"));
    assert_eq!(s.blips.len(), 2);
    let a = &s.blips[0];
    assert_eq!((a.object_id, a.pixel_x, a.pixel_y), (0x10, 70.0, 40.0));
    assert!(a.selected);
    assert_eq!(a.shape, BlipShape::Plus);
    let b = &s.blips[1];
    assert_eq!((b.object_id, b.pixel_x, b.pixel_y), (0xABCD, 60.0, 70.0));
    assert_eq!(b.name, "0x0000ABCD");
    // Six meters below: dimmed.
    assert_eq!(b.color, ac_ui::radar::dim_rgb(Color::WHITE, 0.65));
    let hidden = RadarScene {
        coordinates_on_radar: false,
        ..scene.clone()
    };
    assert_eq!(radar::build_snapshot(&hidden).coordinates_text, None);
    assert!(
        radar::build_snapshot(&RadarScene::default())
            .blips
            .is_empty()
    );
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::import;
    use ac_ui::widgets::VitalsRoot;
    use std::path::PathBuf;

    fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(
            ac_dat::DatCollection::open(dir).expect("open the retail dats"),
        ))
        .expect("master property table")
    }

    fn mounted(c: &DatContent, id: u32) -> (UiHost, ImportedLayout) {
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let layout = import(&mut host.root, c, id, c.font(DEFAULT_FONT_ID)).unwrap();
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        (host, layout)
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_chat_window_imports_builds_binds_and_sends() {
        let c = content();
        let (mut host, layout) = mounted(&c, chat::LAYOUT_ID);
        let ui = &mut host.root;
        let t = layout.find(chat::TRANSCRIPT_ID).unwrap();
        let text = ui.widget::<Text>(t).unwrap();
        assert!(text.selectable());
        assert!(!text.outline);
        assert_eq!(
            text.default_color,
            Color::rgba(204.0 / 255.0, 204.0 / 255.0, 204.0 / 255.0, 1.0)
        );
        let i = layout.find(chat::INPUT_ID).unwrap();
        let f = ui.widget::<Field>(i).unwrap();
        assert!(f.selectable && f.one_line);
        let bar = ui
            .widget::<Scrollbar>(layout.find(chat::TRACK_ID).unwrap())
            .unwrap();
        assert_eq!(bar.track_sprite, 0x0600_4C5F);
        assert_eq!(bar.thumb_top_sprite, 0x0600_4C60);
        assert_eq!(bar.thumb_sprite, 0x0600_4C63);
        assert_eq!(bar.thumb_bot_sprite, 0x0600_4C66);
        assert_eq!(bar.up_sprite, 0x0600_4C6C);
        assert_eq!(bar.down_sprite, 0x0600_4C69);
        assert_eq!(bar.thumb_rollover_sprite, 0x0600_4C64);
        assert_eq!(bar.thumb_pressed_sprite, 0x0600_4C65);
        assert!(
            ui.widget::<Menu>(layout.find(chat::MENU_ID).unwrap())
                .is_some()
        );
        for id in [chat::SEND_ID, chat::MAX_MIN_ID] {
            assert!(ui.widget::<Button>(layout.find(id).unwrap()).is_some());
        }
        let root = layout.find(chat::ROOT_ID).unwrap_or(layout.root);
        assert_eq!(ui.element(root).unwrap().width, 410.0);
        ui.register_window("chat", layout.root, None, 0).unwrap();
        let ctl = ChatWindowController::bind(ui, &layout, c.font(DEFAULT_FONT_ID), None).unwrap();
        for id in chat::LOCKED_TWIN_IDS {
            assert!(!ui.element(layout.find(id).unwrap()).unwrap().visible());
        }
        // The unread indicator starts hidden (its authored default is Ghosted).
        let u = layout.find(chat::UNREAD_INDICATOR_ID).unwrap();
        assert!(!ui.element(u).unwrap().visible());
        host.attach_controller("chat", Box::new(ctl)).unwrap();
        host.with_controller::<ChatWindowController, _>("chat", |c, _| {
            c.push_line(TranscriptLine::plain(1, 0x05, "Welcome to Asheron's Call!"));
        });
        host.tick(0.016, 16, None);
        let list = host.draw((800.0, 600.0), &c);
        assert!(!list.main.is_empty());
        host.take_outputs();
        type_line(&mut host, "hello");
        assert_eq!(sent_lines(&mut host), ["hello"]);
        assert!(c.take_errors().is_empty());
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_vitals_window_imports_binds_and_toggles_detail() {
        let c = content();
        let (mut host, layout) = mounted(&c, vitals::LAYOUT_ID);
        let ui = &mut host.root;
        assert!(ui.widget::<VitalsRoot>(layout.root).is_some());
        for (bar, back, front) in [
            (vitals::HEALTH, 0x0600_7490, 0x0600_7491),
            (vitals::STAMINA, 0x0600_7492, 0x0600_7493),
            (vitals::MANA, 0x0600_7494, 0x0600_7495),
        ] {
            let m = ui.widget::<Meter>(layout.find(bar).unwrap()).unwrap();
            assert!(m.has_detail_overlay());
            assert_eq!(
                (m.detail_back().sprite, m.detail_front().sprite),
                (back, front)
            );
            for o in [m.detail_back(), m.detail_front()] {
                assert_eq!(
                    (o.left_mode, o.top_mode, o.right_mode, o.bottom_mode),
                    (3, 1, 3, 1)
                );
                assert_eq!((o.parent_w, o.parent_h), (150.0, 16.0));
            }
        }
        let h = ui
            .widget::<Meter>(layout.find(vitals::HEALTH).unwrap())
            .unwrap();
        let d = h.detail_back();
        assert_eq!((d.x, d.y, d.w, d.h), (66.0, 0.0, 18.0, 16.0));

        let mut ctl = VitalsController::bind(ui, &layout);
        ctl.apply(
            ui,
            &VitalsView {
                health: vital(0.42, 42, 100),
                ..VitalsView::default()
            },
        );
        assert_eq!(number(ui, &layout, vitals::HEALTH_TEXT), "42/100");

        // Press the body: compact, then detailed (numbers hidden), then back.
        let stamina = layout.find(vitals::STAMINA).unwrap();
        let stamina_text = layout.find(vitals::STAMINA_TEXT).unwrap();
        let press = |ui: &mut UiRoot| {
            let e = ac_ui::event::UiEvent {
                kind: ac_ui::event::kind::MOUSE_DOWN,
                target: Some(layout.root),
                ..ac_ui::event::UiEvent::default()
            };
            ui.send_event(layout.root, &e);
        };
        press(ui);
        assert_eq!(ui.retail_state(layout.root), Some(states::HIDE_DETAIL));
        assert_eq!(ui.retail_state(stamina), Some(states::HIDE_DETAIL));
        assert!(ui.element(stamina_text).unwrap().visible());
        press(ui);
        assert_eq!(ui.retail_state(layout.root), Some(states::SHOW_DETAIL));
        assert!(!ui.element(stamina_text).unwrap().visible());
        for id in [vitals::HEALTH_TEXT, vitals::MANA_TEXT] {
            assert!(!ui.element(layout.find(id).unwrap()).unwrap().visible());
        }
        press(ui);
        assert_eq!(ui.retail_state(layout.root), Some(states::HIDE_DETAIL));
        assert!(ui.element(stamina_text).unwrap().visible());
        host.tick(0.016, 16, None);
        assert!(!host.draw((800.0, 600.0), &c).main.is_empty());
        assert!(c.take_errors().is_empty());
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_radar_imports_with_its_static_art_and_binds() {
        let c = content();
        let (mut host, layout) = mounted(&c, radar::LAYOUT_ID);
        let ui = &mut host.root;
        assert!(ui.widget::<Radar>(layout.root).is_some());
        assert_eq!(
            ui.element(layout.root).unwrap().dat_element_id,
            radar::ROOT_ID
        );
        assert_eq!(ui.children(layout.root).len(), 8);
        let coords = layout.find(radar::COORDINATE_CONTAINER_ID).unwrap();
        assert_eq!(
            ui.widget::<Text>(coords).unwrap().background_sprite,
            0x0600_4CC0
        );
        assert!(
            ui.widget::<Button>(layout.find(radar::LOCK_BUTTON_ID).unwrap())
                .is_some()
        );
        let mut ctl = RadarController::bind(ui, &layout, c.font(DEFAULT_FONT_ID)).unwrap();
        assert_eq!(ctl.coordinate_text(), Some(coords));
        assert!(ui.children(coords).is_empty());
        ctl.apply_snapshot(ui, snap(0.0, Some("42.1N,33.6E"), false));
        assert_eq!(
            ui.widget::<Text>(coords).unwrap().content_lines()[0].text,
            "42.1N,33.6E"
        );
        let lock = layout.find(radar::LOCK_BUTTON_ID).unwrap();
        assert_eq!(
            ui.widget::<Button>(lock).unwrap().active_state(),
            "UnlockedUI"
        );
        ctl.apply_snapshot(ui, snap(0.0, None, true));
        assert_eq!(
            ui.widget::<Button>(lock).unwrap().active_state(),
            "LockedUI"
        );
        host.tick(0.016, 16, None);
        assert!(!host.draw((800.0, 600.0), &c).main.is_empty());
        assert!(c.take_errors().is_empty());
    }
}

// ── Coordinate links: OpenAC PR #224 ChatWindowControllerTests ──

/// The session's own words for each line of a log, as the window gets them.
fn session_lines(log: &ac_core::chat::ChatLog) -> Vec<ac_runtime::communication::ChatLine> {
    log.entries()
        .map(|e| ac_runtime::communication::ChatLine::from_entry(e, false, 0))
        .collect()
}

fn coordinate_clicks(host: &mut UiHost) -> Vec<(f64, f64, String)> {
    host.take_outputs()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Panel(PanelOutput::CoordinateLinkClicked {
                east_west,
                north_south,
                text,
            }) => Some((east_west, north_south, text)),
            _ => None,
        })
        .collect()
}

fn shown_line(host: &mut UiHost, line: usize) -> String {
    with_chat(host, |c, _| c.transcript_layout().lines[line].text.clone())
}

#[test]
fn clicking_a_coordinate_a_plugin_wrote_raises_a_coordinate_link() {
    // ClickingACoordinateAPluginWroteRaisesACoordinateLink.
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut log = ac_core::chat::ChatLog::new();
    log.on_plugin_message(0, "Your corpse is at 42.1N, 33.5W.", 0);
    for line in session_lines(&log) {
        with_chat(&mut host, |c, _| c.push_chat_line(&line));
    }
    frame(&mut host, 16);
    host.take_outputs();
    let column = shown_line(&mut host, 0).find("42.1N").unwrap();

    assert!(!with_chat(&mut host, |c, ui| c.handle_tag_click(ui, Pos::new(0, 2))));
    assert!(with_chat(&mut host, |c, ui| c
        .handle_tag_click(ui, Pos::new(0, column + 3))));

    assert_eq!(
        coordinate_clicks(&mut host),
        [(-33.5, 42.1, "42.1N, 33.5W".to_owned())]
    );
}

#[test]
fn a_coordinate_link_is_named_with_a_decimal_point() {
    // ACoordinateLinkIsNamedTheSameUnderACommaDecimalLocale: the name is
    // written the same everywhere (this client has no locale of its own).
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut log = ac_core::chat::ChatLog::new();
    log.on_plugin_message(
        0,
        r"Go to <COORD:World:42.125S, 33.5E>the bridge<\COORD>.",
        0,
    );
    for line in session_lines(&log) {
        with_chat(&mut host, |c, _| c.push_chat_line(&line));
    }
    frame(&mut host, 16);
    host.take_outputs();
    let column = shown_line(&mut host, 0).find("bridge").unwrap();

    assert!(with_chat(&mut host, |c, ui| c
        .handle_tag_click(ui, Pos::new(0, column))));

    assert_eq!(
        coordinate_clicks(&mut host),
        [(33.5, -42.125, "42.125S, 33.5E".to_owned())]
    );
}

#[test]
fn clicking_a_players_or_the_servers_coordinate_raises_the_pair_it_shows() {
    // ClickingAPlayersOrTheServersCoordinateRaisesThePairItShows: the typed
    // tag's hidden 0N, 0E is never reported; its caption is a pair, and a
    // click reports exactly that pair.
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut log = ac_core::chat::ChatLog::new();
    log.on_local_speech(
        0,
        "Bob",
        r"<COORD:World:0N, 0E>42.1N 33.5E<\COORD>",
        0x5000_000A,
        false,
        2,
    );
    log.on_system_message(0, "Your corpse is at 42.1N, 33.5W.", 0);
    for line in session_lines(&log) {
        with_chat(&mut host, |c, _| c.push_chat_line(&line));
    }
    frame(&mut host, 16);
    host.take_outputs();
    assert_eq!(shown_line(&mut host, 0), "Bob says, \"42.1N 33.5E\"");
    let typed = shown_line(&mut host, 0).find("42.1N").unwrap();
    let server = shown_line(&mut host, 1).find("42.1N").unwrap();

    assert!(with_chat(&mut host, |c, ui| c
        .handle_tag_click(ui, Pos::new(0, typed + 3))));
    assert!(with_chat(&mut host, |c, ui| c
        .handle_tag_click(ui, Pos::new(1, server + 3))));

    assert_eq!(
        coordinate_clicks(&mut host),
        [
            (33.5, 42.1, "42.1N, 33.5E".to_owned()),
            (-33.5, 42.1, "42.1N, 33.5W".to_owned()),
        ]
    );
}

#[test]
fn clicking_a_combat_lines_coordinate_raises_nothing() {
    // ClickingACombatLinesCoordinateRaisesNothing.
    let (mut host, _) = chat_host(0.0, 400.0);
    let mut log = ac_core::chat::ChatLog::new();
    log.on_combat_line(
        0,
        "You hit 3N 4E for 9 points.",
        ac_core::chat::LogTextType::CombatSelf.as_u32(),
        ac_core::chat::CombatLineKind::Info,
    );
    for line in session_lines(&log) {
        with_chat(&mut host, |c, _| c.push_chat_line(&line));
    }
    frame(&mut host, 16);
    host.take_outputs();
    let column = shown_line(&mut host, 0).find("3N").unwrap();

    assert!(!with_chat(&mut host, |c, ui| c
        .handle_tag_click(ui, Pos::new(0, column + 1))));
    assert!(coordinate_clicks(&mut host).is_empty());
}

// ── Tags in every line: OpenAC chat tag fix ChatWindowControllerTests ──

const TAG_GREEN: Color = Color::rgba(0.0, 178.0 / 255.0, 0.0, 1.0);

/// A chat window whose transcript draws tags in the retail green.
fn green_chat_host() -> UiHost {
    let (mut host, _) = chat_host(0.0, 400.0);
    with_chat(&mut host, |c, ui| {
        ui.widget_mut::<Text>(c.transcript()).unwrap().tag_color = Some(TAG_GREEN);
    });
    host
}

fn push_session_lines(host: &mut UiHost, log: &ac_core::chat::ChatLog) {
    for line in session_lines(log) {
        with_chat(host, |c, _| c.push_chat_line(&line));
    }
    frame(host, 16);
}

fn runs_of_line(host: &mut UiHost, line: usize) -> Vec<ac_ui::text::TextRun> {
    with_chat(host, |c, _| {
        c.transcript_layout().runs[line]
            .clone()
            .expect("the line has runs")
    })
}

#[test]
fn a_server_lines_tell_tag_is_hidden_and_its_name_is_green_and_starts_a_tell() {
    // AServerLinesTellTagIsHiddenAndItsNameIsGreenAndStartsATell.
    let mut host = green_chat_host();
    let mut log = ac_core::chat::ChatLog::new();
    log.on_system_message(0, r"<Tell:IIDString:1:Bob>Bob<\Tell> has logged on.", 0);
    push_session_lines(&mut host, &log);

    assert_eq!(shown_line(&mut host, 0), "Bob has logged on.");
    let line_color = with_chat(&mut host, |c, _| c.transcript_layout().lines[0].color);
    let runs = runs_of_line(&mut host, 0);
    assert_eq!((runs[0].text.as_str(), runs[0].color), ("Bob", TAG_GREEN));
    assert_eq!(runs[1].color, line_color);

    assert!(with_chat(&mut host, |c, ui| c.handle_tag_click(ui, Pos::new(0, 1))));
    assert_eq!(field_text(&mut host), "@tell Bob, ");
}

#[test]
fn a_tag_that_is_not_a_tell_in_a_players_line_is_hidden_drawn_in_the_line_colour_and_not_clickable()
{
    // ATagThatIsNotATellInAPlayersLineIsHiddenDrawnInTheLineColourAndNotClickable.
    let mut host = green_chat_host();
    let mut log = ac_core::chat::ChatLog::new();
    log.on_local_speech(
        0,
        "Bob",
        r"look <IID:IIDString:7:Ann>Ann<\IID> here",
        0x5000_000A,
        false,
        2,
    );
    push_session_lines(&mut host, &log);

    let text = shown_line(&mut host, 0);
    assert_eq!(text, "Bob says, \"look Ann here\"");
    let line_color = with_chat(&mut host, |c, _| c.transcript_layout().lines[0].color);
    let runs = runs_of_line(&mut host, 0);
    assert_eq!((runs[0].text.as_str(), runs[0].color), ("Bob", TAG_GREEN));
    let ann: Vec<_> = runs.iter().filter(|r| r.text == "Ann").collect();
    assert_eq!(ann.len(), 1);
    assert_eq!(ann[0].color, line_color);

    let column = text.find("Ann").unwrap();
    assert!(!with_chat(&mut host, |c, ui| c
        .handle_tag_click(ui, Pos::new(0, column + 1))));
    assert!(with_chat(&mut host, |c, ui| c.handle_tag_click(ui, Pos::new(0, 1))));
    assert_eq!(field_text(&mut host), "@tell Bob, ");
}

#[test]
fn the_characters_own_line_hides_its_tags() {
    // TheCharactersOwnLineHidesItsTags.
    let mut host = green_chat_host();
    let mut log = ac_core::chat::ChatLog::new();
    log.on_local_speech(
        0,
        "You",
        r"<Tell:IIDString:7:Ann>Ann<\Tell>, hi",
        0,
        false,
        2,
    );
    push_session_lines(&mut host, &log);

    assert_eq!(shown_line(&mut host, 0), "You say, \"Ann, hi\"");
}
