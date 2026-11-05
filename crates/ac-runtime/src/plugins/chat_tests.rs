//! Chat against the communication owner and the chat path. Cases from
//! OpenAC `RuntimeAutomationSurface` (`CaptureMessages`,
//! `MaximumPluginChatMessages`), `RuntimeChatEntryOwner.Compose` and
//! `ChatCommandRouter.SubmitEntered`, named per test.

use ac_plugin::abi::{Event, calls};
use ac_plugin::interceptors::MAXIMUM_REWRITE_PASSES;

use super::chat::RETAINED_LINES;
use super::tests::{CLOCKS, call, in_world, with_binding};
use super::{LineOrigin, PluginLine, PluginLines, events};
use crate::communication::ChatLine;
use crate::session::{HostEvent, Outbox, SessionCommand, SessionState};

fn system_line(sequence: u64, text: &str) -> ChatLine {
    ChatLine {
        sequence,
        kind: ac_core::chat::ChatKind::System,
        log_text_type: 0,
        combat_kind: None,
        received_unix_ms: 0,
        text: text.to_owned(),
        channel_name: String::new(),
        sender: String::new(),
        sender_guid: 0,
        words: text.to_owned(),
        display_text: text.to_owned(),
        channel_id: 0,
        spans: None,
    }
}

// OpenAC `CaptureMessages` over `MaximumPluginChatMessages` (512): the
// latest lines above the sequence asked, oldest first.
#[test]
fn the_latest_lines_can_be_read_back_above_a_sequence() {
    let mut state = in_world();
    let lines: Vec<HostEvent> = (1..=600)
        .map(|n| HostEvent::Chat(system_line(n, &format!("line {n}"))))
        .collect();
    events::host_events(&mut state, &lines);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let all = call::<calls::ChatCaptured>(b, &0);
        assert_eq!(all.len(), RETAINED_LINES);
        assert_eq!(all[0].sequence, 89);
        assert_eq!(all.last().unwrap().text, "line 600");
        let newer = call::<calls::ChatCaptured>(b, &598);
        assert_eq!(
            newer.iter().map(|l| l.sequence).collect::<Vec<_>>(),
            [599, 600]
        );
        assert!(call::<calls::ChatCaptured>(b, &600).is_empty());
    });
}

// OpenAC `RuntimeChatEntryOwner.Compose`: staged text makes the entry count
// as typed into, and a second compose is refused until it is sent.
#[test]
fn a_composed_line_holds_the_entry_until_sent() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(!call::<calls::ChatIsInputActive>(b, &()));
        assert!(call::<calls::ChatCompose>(b, &"/vt nav".into()));
        assert!(call::<calls::ChatIsInputActive>(b, &()));
        assert!(!call::<calls::ChatCompose>(b, &"again".into()));
        assert_eq!(b.state.communication.chat_entry().draft(), "/vt nav");
    });
}

// OpenAC `ChatCommandRouter.SubmitEntered`: while a plugin intercepts,
// every line past the client's commands is held for the interceptors; a
// passed line is sent on to plugin verbs and the server, a rewrite is
// intercepted again up to the bound.
#[test]
fn lines_are_held_for_the_interceptors_until_they_pass_or_the_bound() {
    let mut lines = PluginLines::default();
    lines.set_verbs(vec!["hello".into()]);
    assert!(!lines.claim("just talking"));
    assert!(lines.claim("/hello"));
    assert_eq!(lines.take(), [PluginLine::Verb("/hello".into())]);
    lines.set_intercepting(true);
    assert!(lines.claim("  just talking "));
    assert_eq!(
        lines.take(),
        [PluginLine::Intercept {
            line: "just talking".into(),
            rewrites: 0,
            origin: LineOrigin::Chat,
        }]
    );
    // Passed: a verb goes to its plugin, anything else on its way.
    lines.resubmit("/hello", 0, true);
    assert!(lines.claim("/hello"));
    assert_eq!(lines.take(), [PluginLine::Verb("/hello".into())]);
    lines.resubmit("just talking", 0, true);
    assert!(!lines.claim("just talking"));
    // A rewrite is asked about again, until the bound.
    lines.resubmit("LOUDER", 1, false);
    assert!(lines.claim("LOUDER"));
    assert!(matches!(
        lines.take().as_slice(),
        [PluginLine::Intercept { rewrites: 1, .. }]
    ));
    lines.resubmit("LAST", MAXIMUM_REWRITE_PASSES, false);
    assert!(!lines.claim("LAST"));
}

// The chat entry's line keeps its talk focus through the interceptors.
#[test]
fn an_entry_line_is_held_with_its_talk_focus() {
    let mut state = in_world();
    state.plugin_lines.set_intercepting(true);
    let mut outbox = Outbox::new();
    let command = SessionCommand::ChatInput {
        text: "hi all".into(),
        channel: crate::communication::ChatChannelKind::Fellowship,
        tell_target: None,
        tell_target_guid: 0,
    };
    assert!(state.command(command, &mut outbox, true, CLOCKS).is_none());
    assert_eq!(
        state.plugin_lines.take(),
        [PluginLine::Intercept {
            line: "hi all".into(),
            rewrites: 0,
            origin: LineOrigin::Entry {
                channel: crate::communication::ChatChannelKind::Fellowship,
                tell_target: None,
                tell_target_guid: 0,
            },
        }]
    );
    assert!(outbox.is_empty());
}

// A line typed into the chat window's entry aimed at a tell keeps that
// focus through the interceptors (OpenAC `ChatCommandRouter.SubmitEntered`
// routes it with the entry's channel): passed, it goes out as the same tell
// it would have been with no interceptor, never as speech.
#[test]
fn a_tell_typed_into_the_chat_entry_stays_a_tell_through_the_interceptors() {
    use crate::communication::{ChatChannelKind, ChatEntryCommand as E};
    let aim = |state: &mut SessionState, outbox: &mut Outbox| {
        for command in [
            E::SetChannel(ChatChannelKind::Tell),
            E::SetTellTarget {
                name: "Bob".into(),
                guid: 0x5000_0002,
            },
        ] {
            state.command(SessionCommand::ChatEntry(command), outbox, true, CLOCKS);
        }
    };
    let submit = |state: &mut SessionState, outbox: &mut Outbox| {
        let line = E::Submit(Some("meet me".into()));
        state.command(SessionCommand::ChatEntry(line), outbox, true, CLOCKS);
    };
    let bodies = |outbox: &mut Outbox| outbox.drain().map(|o| o.body).collect::<Vec<_>>();

    // No interceptor: the tell goes out at once.
    let mut direct = in_world();
    let mut outbox = Outbox::new();
    aim(&mut direct, &mut outbox);
    submit(&mut direct, &mut outbox);
    let tell = bodies(&mut outbox);
    assert_eq!(tell.len(), 1);

    // An interceptor: the line is held with the entry's talk focus.
    let mut state = in_world();
    state.plugin_lines.set_intercepting(true);
    let mut outbox = Outbox::new();
    aim(&mut state, &mut outbox);
    submit(&mut state, &mut outbox);
    assert!(outbox.is_empty());
    let origin = LineOrigin::Entry {
        channel: ChatChannelKind::Tell,
        tell_target: Some("Bob".into()),
        tell_target_guid: 0x5000_0002,
    };
    assert_eq!(
        state.plugin_lines.take(),
        [PluginLine::Intercept {
            line: "meet me".into(),
            rewrites: 0,
            origin: origin.clone(),
        }]
    );
    // Passed, it is sent on with that focus, as the session's runner does.
    state.plugin_lines.resubmit("meet me", 0, true);
    let LineOrigin::Entry {
        channel,
        tell_target,
        tell_target_guid,
    } = origin
    else {
        unreachable!()
    };
    let passed = SessionCommand::ChatInput {
        text: "meet me".into(),
        channel,
        tell_target,
        tell_target_guid,
    };
    state.command(passed, &mut outbox, true, CLOCKS);
    assert_eq!(bodies(&mut outbox), tell);
}

// A map link clicked in the transcript reaches plugins (OpenAC main
// `RuntimeAutomationSurface.RaiseChatLinkClicked`).
#[test]
fn a_clicked_map_link_reaches_plugins() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let click = SessionCommand::ChatLinkClicked {
        east_west: -12.5,
        north_south: 40.25,
        text: "40.25N, 12.5W".into(),
    };
    assert!(state.command(click, &mut outbox, true, CLOCKS).is_none());
    let heard: Vec<Event> = events::follow(&mut state, true, true)
        .into_iter()
        .filter(|e| matches!(e, Event::ChatLinkClicked(_)))
        .collect();
    let [Event::ChatLinkClicked(link)] = heard.as_slice() else {
        panic!("one link, got {heard:?}");
    };
    assert_eq!(link.kind, 0);
    assert_eq!(link.display_text, "40.25N, 12.5W");
    assert_eq!(link.coordinate, Some((-12.5, 40.25)));
}

// A line a plugin call wrote is handed to the plugins
// right after the call, once, retained once, and the publication of the
// pass does not hand it out again; a submit is carried out at once.
#[test]
fn a_line_a_call_wrote_is_heard_once_right_after_it() {
    use ac_plugin::SessionApi;
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let written = with_binding(&mut state, &mut outbox, |b| {
        b.post_chat("Mosswart Massacre has started!", 0);
        let heard = b.new_chat_events();
        let [Event::ChatReceived(line)] = heard.as_slice() else {
            panic!("one line, got {heard:?}");
        };
        assert_eq!(line.text, "Mosswart Massacre has started!");
        assert!(b.new_chat_events().is_empty());
        // A line submitted is carried out now (the client's `/help`
        // answers locally); what it writes is heard next.
        let mut submits = vec!["/help".to_owned()];
        b.submit_now(&mut submits);
        assert!(submits.is_empty());
        let written = b.new_chat_events().len();
        assert!(written > 0);
        assert!(b.new_chat_events().is_empty());
        1 + written
    });
    let published = state.drain_host_events();
    let early = published
        .iter()
        .filter(|e| {
            matches!(e, HostEvent::Chat(l) if state.plugin_services.chat.heard_early(l.sequence))
        })
        .count();
    assert_eq!(early, written, "every line was heard early: {published:?}");
    events::host_events(&mut state, &published);
    state.plugin_services.chat.published();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        // Retained once each.
        assert_eq!(call::<calls::ChatCaptured>(b, &0).len(), written);
    });
}

// OpenAC PR #224 RuntimeAutomationSurfacePluginApiTests
// .PostMessageMarksTheLineAsCarryingMarkupAndServerTextDoesNot.
#[test]
fn a_plugins_post_carries_markup_and_server_text_does_not() {
    use ac_core::chat::LogTextType;
    use ac_plugin::SessionApi;
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let before = state.communication.chat().entries().len();
    with_binding(&mut state, &mut outbox, |b| {
        b.post_chat(
            r"<COORD:World:1N, 2E>here<\COORD>",
            LogTextType::Magic.as_u32(),
        );
        b.post_chat(r"<COORD:World:1N, 2E>there<\COORD>", 0);
    });
    state.communication.show_text(
        CLOCKS.wall_unix_ms,
        r"<COORD:World:1N, 2E>server<\COORD>",
        LogTextType::Default,
    );
    let marked: Vec<bool> = state
        .communication
        .chat()
        .entries()
        .skip(before)
        .map(|e| e.allows_markup)
        .collect();
    assert_eq!(marked, [true, true, false]);
}

// OpenAC PR #224 RuntimeAutomationSurfacePluginApiTests
// .APluginsStatusLinePostShowsTheCaptionNotTheTag: a plugin's status-line
// class goes to the transcript, where the tag is a link; the status line
// itself, which has no links, shows only the caption.
#[test]
fn a_plugins_status_line_post_shows_the_caption_not_the_tag() {
    use ac_core::chat::LogTextType;
    use ac_plugin::SessionApi;
    const POSTED: &str = r"Go to <COORD:World:1N, 2E>the bridge<\COORD> now.";
    let mut state = in_world();
    let mut outbox = Outbox::new();
    let before = state.communication.chat().entries().len();
    with_binding(&mut state, &mut outbox, |b| {
        b.post_chat(POSTED, LogTextType::ClientLocal.as_u32());
    });
    state
        .communication
        .show_plugin_text(CLOCKS.wall_unix_ms, POSTED, LogTextType::ClientLocal);
    let entries: Vec<_> = state.communication.chat().entries().skip(before).collect();
    assert_eq!(entries.len(), 1);
    assert!(entries[0].allows_markup);
    assert_eq!(
        crate::communication::format(entries[0]),
        "Go to the bridge now."
    );
    let mut spew = state.communication.spew_box().clone();
    spew.tick(0.0);
    assert_eq!(
        spew.visible().last().map(|e| e.text.as_str()),
        Some("Go to the bridge now.")
    );
}

// OpenAC `IEvents.StatusTextReceived`: the server's transient text (here a
// corpse another player has open) reaches the plugins as a status notice,
// in order with the chat lines around it, and never as a chat line.

const BUSY: &str = "The Corpse of War Armoredillo is already in use by Unkle Leo!";

/// The server's transient string `text`, applied to `state`.
fn transient(state: &mut crate::session::SessionState, text: &str) {
    let mut w = ac_bits::Writer::new();
    w.u32(0xF7B0);
    w.u32(super::tests::PLAYER);
    w.u32(1);
    w.u32(ac_proto::GameEventType::COMMUNICATION_TRANSIENT_STRING.0);
    w.string16l(text).unwrap();
    let body = w.into_vec();
    let message = ac_proto::Inbound::parse(&body).expect("a game event");
    state.apply(&message, &mut Outbox::new(), true, CLOCKS);
}

/// The chat events, each as `kind: text`.
fn chat_events(events: &[Event]) -> Vec<String> {
    events
        .iter()
        .filter_map(|e| match e {
            Event::ChatReceived(line) => Some(format!("chat: {}", line.text)),
            Event::StatusTextReceived(text) => Some(format!("status: {text}")),
            _ => None,
        })
        .collect()
}

/// A state in the world with nothing queued for the host.
fn quiet_in_world() -> crate::session::SessionState {
    let mut state = in_world();
    let _ = state.drain_host_events();
    state.plugin_services.chat.published();
    state
}

#[test]
fn a_transient_string_reaches_the_plugins_as_a_status_notice_in_order() {
    use ac_core::chat::LogTextType;
    let mut state = quiet_in_world();
    let wall = CLOCKS.wall_unix_ms;
    state
        .communication
        .show_text(wall, "Before.", LogTextType::Default);
    transient(&mut state, BUSY);
    state
        .communication
        .show_text(wall, "After.", LogTextType::Default);
    let published = state.drain_host_events();
    let heard = super::chat::published_chat(&state.plugin_services.chat, &published);
    assert_eq!(
        chat_events(&heard),
        [
            "chat: Before.".to_owned(),
            format!("status: {BUSY}"),
            "chat: After.".to_owned(),
        ]
    );
    // Not a transcript line, so not a line plugins can read back.
    events::host_events(&mut state, &published);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        let kept = call::<calls::ChatCaptured>(b, &0);
        assert!(kept.iter().all(|l| l.text != BUSY), "{kept:?}");
    });
}

#[test]
fn a_status_notice_heard_right_after_a_call_is_not_handed_out_again() {
    use ac_plugin::SessionApi;
    let mut state = quiet_in_world();
    let wall = CLOCKS.wall_unix_ms;
    state.communication.show_interface_text(wall, "first");
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            chat_events(&b.new_chat_events()),
            ["status: first".to_owned()]
        );
        assert!(b.new_chat_events().is_empty());
        b.state.communication.show_interface_text(wall, "second");
        assert_eq!(
            chat_events(&b.new_chat_events()),
            ["status: second".to_owned()]
        );
    });
    state.communication.show_interface_text(wall, "third");
    let published = state.drain_host_events();
    let heard = super::chat::published_chat(&state.plugin_services.chat, &published);
    assert_eq!(chat_events(&heard), ["status: third".to_owned()]);
    state.plugin_services.chat.published();
    state.communication.show_interface_text(wall, "fourth");
    let published = state.drain_host_events();
    let heard = super::chat::published_chat(&state.plugin_services.chat, &published);
    assert_eq!(chat_events(&heard), ["status: fourth".to_owned()]);
}

// A session ending by disconnect hears a notice right after the leaving's
// call, then hands its last host events straight to the host, unpublished;
// the plugins and the state live on into the next connection, whose
// notices must all still reach them.
#[test]
fn a_notice_heard_early_then_drained_unpublished_hides_no_later_notice() {
    use ac_plugin::SessionApi;
    let mut state = quiet_in_world();
    let wall = CLOCKS.wall_unix_ms;
    state
        .communication
        .show_interface_text(wall, "while leaving");
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(
            chat_events(&b.new_chat_events()),
            ["status: while leaving".to_owned()]
        );
    });
    let _unpublished = state.drain_host_events();
    transient(&mut state, BUSY);
    let published = state.drain_host_events();
    let heard = super::chat::published_chat(&state.plugin_services.chat, &published);
    assert_eq!(chat_events(&heard), [format!("status: {BUSY}")]);
}
