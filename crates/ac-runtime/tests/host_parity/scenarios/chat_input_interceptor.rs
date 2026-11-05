//! A plugin intercepting what the player types (OpenAC
//! `Scenarios/ChatInputInterceptorParityTests.cs`): a rewritten tell, a
//! suppressed line, a client command never offered, a rewrite loop bounded,
//! and an interceptor that faults. Each step asserts the words that reached
//! the wire, or that none did.
//!
//! A line is typed the way a person does: into the chat entry, submitted.

use ac_plugin::abi::calls;
use ac_plugin::interceptors::MAXIMUM_REWRITE_PASSES;
use ac_runtime::communication::ChatEntryCommand;
use ac_runtime::session::{HostEvent, SessionCommand};

use crate::host_parity::arm::{Arm, PARITY, run};
use crate::host_parity::scenarios::item::one;
use crate::host_parity::server::Outbound;
use crate::host_parity::transcript::Transcript;

const TALK_ACTION: u32 = 0x0015;
const TELL_ACTION: u32 = 0x005D;
/// The callback tag of an input interceptor (`Callback::ChatIntercept`).
const INTERCEPT: u32 = 1;

/// Registers an interceptor through the relay; its handle.
pub fn add_interceptor(arm: &mut Arm) -> u32 {
    arm.call::<calls::ChatAddInterceptor>(&())
        .unwrap_or_else(|why| panic!("{}: no interceptor: {why}", arm.name()))
}

/// Submits a line through the chat entry and waits until the session has
/// carried it out.
pub fn submit(arm: &mut Arm, line: &str) {
    arm.command(SessionCommand::ChatEntry(ChatEntryCommand::Submit(Some(
        line.to_owned(),
    ))));
    // A call after it: the line was carried out (the session takes typed
    // lines before the plugins' calls of the same pass).
    let _ = arm.call::<calls::HostSettings>(&());
}

/// Types a line and records what the client sent for it.
fn type_line(arm: &mut Arm, transcript: &mut Transcript, line: &str) -> Vec<Outbound> {
    submit(arm, line);
    arm.record_outbound(transcript)
}

fn the_spoken_line(sent: &[Outbound]) -> String {
    one(sent, TALK_ACTION).text_at(12)
}

fn the_told_line(sent: &[Outbound]) -> String {
    one(sent, TELL_ACTION).text_at(12)
}

/// Lines offered to the relay's interceptor so far.
fn offered(arm: &Arm) -> usize {
    arm.logs
        .iter()
        .filter(|(p, m)| p == PARITY && m.starts_with("parity callback 01000000"))
        .count()
}

/// Client-local text put to the player so far.
fn notices(arm: &Arm) -> usize {
    arm.host_events
        .iter()
        .filter(|e| matches!(e, HostEvent::InterfaceText(_)))
        .count()
}

fn text(s: &str) -> Vec<u8> {
    s.as_bytes().to_vec()
}

// OpenAC `ChatInputInterceptorParityTests.ARewrittenTellReachesTheWireRewrittenOnBothClients`.
#[test]
fn a_rewritten_tell_reaches_the_wire_rewritten_on_both_clients() {
    run(|arm, transcript| {
        let handle = add_interceptor(arm);
        arm.rule(
            INTERCEPT,
            handle,
            "replace",
            &[&text("[loc]"), &text("12.3N, 45.6E")],
        );

        transcript.step("tell with a marker");
        let sent = type_line(arm, transcript, "/tell Bob, meet me at [loc]");
        assert_eq!(
            the_told_line(&sent),
            "meet me at 12.3N, 45.6E",
            "{}",
            arm.name()
        );

        transcript.step("the plugin lets go");
        assert!(arm.call::<calls::ChatRemoveInterceptor>(&handle));
        let _ = arm.outbound();
        let after = type_line(arm, transcript, "/tell Bob, back at [loc]");
        assert_eq!(the_told_line(&after), "back at [loc]");
    });
}

// OpenAC `ChatInputInterceptorParityTests.ASuppressedLineLeavesNoTraceOnEitherClient`.
#[test]
fn a_suppressed_line_leaves_no_trace_on_either_client() {
    run(|arm, transcript| {
        let handle = add_interceptor(arm);
        arm.rule(INTERCEPT, handle, "suppress-prefix", &[&text("!")]);
        let (feed, notices_before) = (arm.chat.len(), notices(arm));

        transcript.step("a swallowed line");
        let sent = type_line(arm, transcript, "!secret");
        transcript.record("feed.grew", arm.chat.len() - feed);
        transcript.record("notices", notices(arm) - notices_before);
        assert!(sent.is_empty(), "{}: {sent:?}", arm.name());
        assert_eq!(arm.chat.len(), feed);
        assert_eq!(notices(arm), notices_before);

        transcript.step("a line the plugin passes");
        let spoken = type_line(arm, transcript, "hello");
        assert_eq!(the_spoken_line(&spoken), "hello");
    });
}

// OpenAC `ChatInputInterceptorParityTests.AClientCommandIsNeverOfferedToAnInterceptorOnEitherClient`.
#[test]
fn a_client_command_is_never_offered_to_an_interceptor_on_either_client() {
    run(|arm, transcript| {
        let handle = add_interceptor(arm);
        arm.rule(INTERCEPT, handle, "suppress-all", &[]);
        let feed = arm.chat.len();

        transcript.step("a client command");
        submit(arm, "/version");
        arm.sync();
        transcript.record("offered", offered(arm));
        let grew = arm.chat.len() - feed;
        transcript.record("feed.grew", grew > 0);
        assert_eq!(offered(arm), 0, "{}", arm.name());
        // The command really ran and answered the player.
        assert!(grew > 0, "{} did not run the command", arm.name());

        transcript.step("plain speech is still intercepted");
        let sent = type_line(arm, transcript, "hello");
        transcript.record("offered", offered(arm));
        assert_eq!(offered(arm), 1);
        assert!(sent.is_empty());
    });
}

// OpenAC `ChatInputInterceptorParityTests.ARewriteLoopIsBoundedTheSameOnBothClients`.
#[test]
fn a_rewrite_loop_is_bounded_the_same_on_both_clients() {
    run(|arm, transcript| {
        let handle = add_interceptor(arm);
        arm.rule(INTERCEPT, handle, "append", &[&text("!")]);

        transcript.step("a line that never stops growing");
        submit(arm, "hello");
        // Each rewrite is typed again and offered again on the next pass.
        arm.until("the line goes out", |a| {
            a.server
                .pending()
                .iter()
                .any(|m| m.game_action() == Some(TALK_ACTION))
        });
        let sent = arm.record_outbound(transcript);
        let expected = format!("hello{}", "!".repeat(MAXIMUM_REWRITE_PASSES as usize));
        assert_eq!(the_spoken_line(&sent), expected, "{}", arm.name());
    });
}

// OpenAC `ChatInputInterceptorParityTests.AThrowingInterceptorDoesNotBreakChatOnEitherClient`:
// a plugin whose callback faults is stopped (a trap ends a WebAssembly
// plugin), and the line goes out regardless.
#[test]
fn a_faulting_interceptor_does_not_break_chat_on_either_client() {
    run(|arm, transcript| {
        let handle = add_interceptor(arm);
        arm.rule(INTERCEPT, handle, "trap", &[]);
        let notices_before = notices(arm);

        transcript.step("say hello past a broken interceptor");
        arm.command(SessionCommand::ChatEntry(ChatEntryCommand::Submit(Some(
            "hello".to_owned(),
        ))));
        // The relay is gone; the barrier is the server's ping.
        arm.wait(std::time::Duration::from_millis(100));
        let sent = arm.record_outbound(transcript);
        transcript.record("notices", notices(arm) - notices_before);
        assert_eq!(the_spoken_line(&sent), "hello", "{}", arm.name());
        assert_eq!(notices(arm), notices_before);
    });
}
