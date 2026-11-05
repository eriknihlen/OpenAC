//! The one chat entry every front end types into, on both arms (OpenAC
//! `Scenarios/ChatEntryParityTests.cs`): staging a line, sending while a
//! draft is pending, recalling, where a plain line goes, a reply with no
//! one to reply to, and a channel tag with no command behind it.
//!
//! OpenAC reads the draft off the entry owner and records what its bus was
//! handed; here the draft is read the one way it leaves the session (sent
//! with an empty submit) and what was sent is read off the wire.

use ac_plugin::abi::calls;
use ac_runtime::communication::{ChatChannelKind, ChatEntryCommand};
use ac_runtime::session::{HostEvent, SessionCommand};

use crate::host_parity::arm::{Arm, run, run_with};
use crate::host_parity::scenarios::item::{one, sent};
use crate::host_parity::server::Outbound;
use crate::host_parity::transcript::Transcript;

pub const TALK_ACTION: u32 = 0x0015;
pub const TELL_ACTION: u32 = 0x005D;
pub const CHANNEL_ACTION: u32 = 0x0147;
const LISTENER: u32 = 0x5000_000A;

pub fn entry(arm: &mut Arm, command: ChatEntryCommand) {
    arm.command(SessionCommand::ChatEntry(command));
    // The session has carried it out once a call made after it answers.
    let _ = arm.call::<calls::HostSettings>(&());
}

fn submit(arm: &mut Arm, line: Option<&str>) {
    entry(arm, ChatEntryCommand::Submit(line.map(str::to_owned)));
}

fn record_sends(transcript: &mut Transcript, sends: &[Outbound]) {
    transcript.record("sends", sends.len());
    for (index, message) in sends.iter().enumerate() {
        transcript.record(&format!("send{index}"), message);
    }
}

/// The text of a talk, tell or channel line, wherever it sits.
pub fn said(message: &Outbound) -> String {
    match message.game_action() {
        Some(CHANNEL_ACTION) => message.text_at(16),
        _ => message.text_at(12),
    }
}

// OpenAC `ChatEntryParityTests.AClientWithNoWindowStagesTheLineForReal`:
// the plugin's compose stages the line in the session's entry whatever is
// attached, and the keyboard goes to it.
#[test]
fn a_client_stages_the_line_for_real() {
    run_with(&Default::default(), &[], true, |arm, transcript| {
        transcript.step("compose");
        let before = arm.call::<calls::ChatIsInputActive>(&());
        let accepted = arm.call::<calls::ChatCompose>(&"hello".to_owned());
        let after = arm.call::<calls::ChatIsInputActive>(&());
        transcript.record("before", before);
        transcript.record("accepted", accepted);
        transcript.record("after", after);
        assert!(!before && accepted && after, "{}", arm.name());
    });
}

// OpenAC `ChatEntryParityTests.StagingALineWithoutSendingItWorksTheSameOnBothClients`.
#[test]
fn staging_a_line_without_sending_it_works_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("before");
        let active = arm.call::<calls::ChatIsInputActive>(&());
        transcript.record("inputActive", active);
        assert!(!active);

        transcript.step("compose");
        let accepted = arm.call::<calls::ChatCompose>(&"hello".to_owned());
        let active = arm.call::<calls::ChatIsInputActive>(&());
        transcript.record("accepted", accepted);
        transcript.record("inputActive", active);
        assert!(accepted, "{} would not stage the line", arm.name());
        assert!(active);

        transcript.step("submit");
        submit(arm, None);
        let active = arm.call::<calls::ChatIsInputActive>(&());
        transcript.record("inputActive", active);
        let sends = arm.outbound();
        record_sends(transcript, &sends);
        // What was staged is what went out, and the box gave the keyboard
        // back.
        assert_eq!(said(&one(&sends, TALK_ACTION)), "hello", "{}", arm.name());
        assert!(!active);
    });
}

// OpenAC `ChatEntryParityTests.SendingWhileADraftIsPendingBehavesTheSameOnBothClients`.
#[test]
fn sending_while_a_draft_is_pending_behaves_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("compose");
        let staged = arm.call::<calls::ChatCompose>(&"half a sentence".to_owned());
        transcript.record("accepted", staged);
        assert!(staged, "{} would not stage the line", arm.name());

        transcript.step("submit-something-else");
        submit(arm, Some("hello"));
        let sends = arm.outbound();
        record_sends(transcript, &sends);
        assert_eq!(said(&one(&sends, TALK_ACTION)), "hello", "{}", arm.name());

        transcript.step("compose-again");
        // The pending draft is gone, so staging is allowed again.
        let again = arm.call::<calls::ChatCompose>(&"second".to_owned());
        transcript.record("accepted", again);
        assert!(again, "{} would not stage a second line", arm.name());
        submit(arm, None);
        let sends = arm.outbound();
        record_sends(transcript, &sends);
        assert_eq!(said(&one(&sends, TALK_ACTION)), "second");
    });
}

// OpenAC `ChatEntryParityTests.RecallingAnEarlierLineWorksTheSameOnBothClients`.
#[test]
fn recalling_an_earlier_line_works_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("send-three");
        for line in ["first", "second", "third"] {
            submit(arm, Some(line));
        }
        let sends = arm.outbound();
        record_sends(transcript, &sends);

        transcript.step("recall");
        entry(arm, ChatEntryCommand::RecallPrevious);
        entry(arm, ChatEntryCommand::RecallPrevious);

        transcript.step("send-the-recalled-line");
        submit(arm, None);
        let resent = arm.outbound();
        record_sends(transcript, &resent);
        let all: Vec<String> = sends
            .iter()
            .chain(&resent)
            .filter(|m| m.game_action() == Some(TALK_ACTION))
            .map(said)
            .collect();
        // Backwards through what was said, latest first: the second step
        // back is the second line, and it went out again.
        assert_eq!(
            all,
            ["first", "second", "third", "second"],
            "{}",
            arm.name()
        );
    });
}

// OpenAC `ChatEntryParityTests.WhereAPlainLineGoesIsDecidedTheSameWayOnBothClients`.
#[test]
fn where_a_plain_line_goes_is_decided_the_same_way_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("say");
        submit(arm, Some("hello"));

        transcript.step("to-a-channel");
        entry(
            arm,
            ChatEntryCommand::SetChannel(ChatChannelKind::Fellowship),
        );
        submit(arm, Some("group up"));

        transcript.step("to-one-listener");
        entry(
            arm,
            ChatEntryCommand::SetTellTarget {
                name: "Bob".into(),
                guid: LISTENER,
            },
        );
        submit(arm, Some("hi"));

        transcript.step("sent");
        let sends = arm.outbound();
        record_sends(transcript, &sends);
        let lines: Vec<(Option<u32>, String)> =
            sends.iter().map(|m| (m.game_action(), said(m))).collect();
        assert_eq!(lines.len(), 3, "{}: {sends:?}", arm.name());
        assert_eq!(lines[0], (Some(TALK_ACTION), "hello".to_owned()));
        assert_eq!(lines[1], (Some(CHANNEL_ACTION), "group up".to_owned()));
        // The tell to a listener picked in the world goes to that object.
        assert_eq!(lines[2].1, "hi");
    });
}

/// Client-local text the arm put to the player so far.
pub fn interface_text(arm: &Arm) -> Vec<String> {
    arm.host_events
        .iter()
        .filter_map(|e| match e {
            HostEvent::InterfaceText(text) => Some(text.clone()),
            _ => None,
        })
        .collect()
}

// OpenAC `ChatEntryParityTests.AReplyWithNoOneToReplyToIsRefusedTheSameWayOnBothClients`.
#[test]
fn a_reply_with_no_one_to_reply_to_is_refused_the_same_way_on_both_clients() {
    run(|arm, transcript| {
        let said_before = interface_text(arm).len();
        transcript.step("reply");
        submit(arm, Some("/r hi"));
        let sends = arm.outbound();
        record_sends(transcript, &sends);
        assert!(sends.is_empty(), "{}: {sends:?}", arm.name());

        transcript.step("what-the-player-reads");
        let told: Vec<String> = interface_text(arm)[said_before..].to_vec();
        let chat: Vec<String> = arm
            .chat
            .iter()
            .map(|c| c.text.clone())
            .filter(|t| !t.contains(crate::host_parity::world::SYNC_MARKER))
            .collect();
        transcript.record("interfaceText", &told);
        transcript.record("chat", &chat);
        // It really had a reason to give.
        assert!(!told.is_empty() || !chat.is_empty(), "{}", arm.name());
    });
}

// OpenAC `ChatEntryParityTests.AChannelTagWithNoCommandBehindItFallsBackTheSameOnBothClients`.
#[test]
fn a_channel_tag_with_no_command_behind_it_falls_back_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("channel-tag");
        submit(arm, Some("@f general x"));
        let sends = arm.outbound();
        record_sends(transcript, &sends);
        // The tag picks the channel and the rest is what is said on it.
        assert_eq!(sends.len(), 1, "{}: {sends:?}", arm.name());
        assert_eq!(said(&one(&sends, CHANNEL_ACTION)), "general x");
        assert!(sent(&sends, TALK_ACTION).is_empty());
    });
}
