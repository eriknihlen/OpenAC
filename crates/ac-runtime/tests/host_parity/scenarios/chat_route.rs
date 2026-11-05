//! Typing a line, through the session's own route, on both arms (OpenAC
//! `Scenarios/ChatRouteParityTests.cs`): what was sent for it, byte for
//! byte, every line that reached the feed in order, and what the client
//! said to the player in its own words.
//!
//! Not ported: `ALineFilteredOutOfTheWindowIsShownOnNeitherClient`. A chat
//! window and its filters are the window's own state in this client (a
//! session without one shows no chat window at all); the feed both arms
//! share carries both lines, which `a_feed_subscribed_to_late_replays_the_same`
//! reads.

use std::sync::Arc;

use ac_plugin::abi::calls;
use ac_runtime::communication::{ChatEntryCommand, ChatPose, ChatPoseCatalog};
use ac_runtime::gameplay::GameTables;

use crate::host_parity::arm::{Arm, Setup, run, run_with};
use crate::host_parity::scenarios::chat_entry::{
    CHANNEL_ACTION, TALK_ACTION, TELL_ACTION, entry, interface_text, said,
};
use crate::host_parity::scenarios::item::one;
use crate::host_parity::server::Outbound;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

/// The feed lines since `from`, without the server's sync markers.
fn feed(arm: &Arm, from: usize) -> Vec<String> {
    arm.chat[from..]
        .iter()
        .map(|c| c.text.clone())
        .filter(|t| !t.contains(world::SYNC_MARKER))
        .collect()
}

fn record_feed(transcript: &mut Transcript, arm: &Arm, from: usize) {
    let lines = feed(arm, from);
    transcript.record("feed.count", lines.len());
    for (index, line) in lines.iter().enumerate() {
        transcript.record(&format!("feed[{index}].text"), line);
    }
}

fn record_interface_text(transcript: &mut Transcript, arm: &Arm, from: usize) -> Vec<String> {
    let told = interface_text(arm)[from..].to_vec();
    transcript.record("interface.count", told.len());
    for (index, line) in told.iter().enumerate() {
        transcript.record(&format!("interface[{index}]"), line);
    }
    told
}

/// Types one line into the chat entry and records what reached the feed
/// and the wire for it.
fn type_line(arm: &mut Arm, transcript: &mut Transcript, line: &str) -> Vec<Outbound> {
    let from = arm.chat.len();
    entry(arm, ChatEntryCommand::Submit(Some(line.to_owned())));
    arm.sync();
    record_feed(transcript, arm, from);
    arm.record_outbound(transcript)
}

// OpenAC `ChatRouteParityTests.SayingSomethingGoesOutTheSameWayOnBothClients`.
#[test]
fn saying_something_goes_out_the_same_way_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("say hello");
        let sent = type_line(arm, transcript, "hello");
        assert_eq!(said(&one(&sent, TALK_ACTION)), "hello", "{}", arm.name());
    });
}

// OpenAC `ChatRouteParityTests.TellingOneListenerGoesOutTheSameWayOnBothClients`.
#[test]
fn telling_one_listener_goes_out_the_same_way_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("tell Bob");
        let sent = type_line(arm, transcript, "@tell Bob, hi");
        assert_eq!(said(&one(&sent, TELL_ACTION)), "hi", "{}", arm.name());
    });
}

// OpenAC `ChatRouteParityTests.AReplyWithNoOneToReplyToIsRefusedTheSameWayOnBothClients`.
#[test]
fn a_reply_with_no_one_to_reply_to_is_refused_the_same_way_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("reply to nobody");
        let (told_from, feed_from) = (interface_text(arm).len(), arm.chat.len());
        let sent = type_line(arm, transcript, "/r hi");
        let told = record_interface_text(transcript, arm, told_from);
        assert!(sent.is_empty(), "{}: {sent:?}", arm.name());
        // It had something to say, in the feed or as the client's text.
        assert!(
            !told.is_empty() || !feed(arm, feed_from).is_empty(),
            "{}",
            arm.name()
        );
    });
}

// OpenAC `ChatRouteParityTests.APoseInsideALineOfSpeechRunsTheSameOnBothClients`.
#[test]
fn a_pose_inside_a_line_of_speech_runs_the_same_on_both_clients() {
    let poses = ChatPoseCatalog::of([(
        "wave".to_owned(),
        ChatPose {
            motion_command: 0x1300_0026,
            self_text: "wave.".to_owned(),
            others_text: "waves %p hand.".to_owned(),
        },
    )]);
    let setup = Setup {
        tables: Some(Arc::new(GameTables::empty().with_chat_poses(poses))),
        ..Setup::default()
    };
    run_with(&setup, &[], true, |arm, transcript| {
        transcript.step("say hello with a wave");
        let from = arm.chat.len();
        let sent = type_line(arm, transcript, "hello *wave*");
        // The words went out, and the speaker's own line about the pose is
        // in the feed.
        assert_eq!(said(&one(&sent, TALK_ACTION)), "hello", "{}", arm.name());
        assert!(
            feed(arm, from).iter().any(|l| l.contains("You wave")),
            "{}: {:?}",
            arm.name(),
            feed(arm, from)
        );
    });
}

// OpenAC `ChatRouteParityTests.AChannelTagWithNoCommandFallsBackTheSameOnBothClients`.
#[test]
fn a_channel_tag_with_no_command_falls_back_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("a channel tag");
        let told_from = interface_text(arm).len();
        let sent = type_line(arm, transcript, "@f general x");
        record_interface_text(transcript, arm, told_from);
        assert_eq!(sent.len(), 1, "{}: {sent:?}", arm.name());
        assert_eq!(said(&one(&sent, CHANNEL_ACTION)), "general x");
    });
}

// OpenAC `ChatRouteParityTests.StagingALineThenSendingItRunsTheSameOnBothClients`.
#[test]
fn staging_a_line_then_sending_it_runs_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("stage it");
        let accepted = arm.call::<calls::ChatCompose>(&"x".to_owned());
        transcript.record("accepted", accepted);
        transcript.record("inputActive", arm.call::<calls::ChatIsInputActive>(&()));

        transcript.step("send what is staged");
        let from = arm.chat.len();
        entry(arm, ChatEntryCommand::Submit(None));
        arm.sync();
        record_feed(transcript, arm, from);
        let sent = arm.record_outbound(transcript);
        assert!(!sent.is_empty(), "{}", arm.name());
    });
}

// OpenAC `ChatRouteParityTests.SendingWhileSomethingIsStagedRunsTheSameOnBothClients`.
#[test]
fn sending_while_something_is_staged_runs_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("stage half a sentence");
        let staged = arm.call::<calls::ChatCompose>(&"half a sentence".to_owned());
        transcript.record("accepted", staged);
        assert!(staged, "{} would not stage the line", arm.name());

        transcript.step("send something else");
        let sent = type_line(arm, transcript, "hello");
        assert_eq!(said(&one(&sent, TALK_ACTION)), "hello", "{}", arm.name());

        transcript.step("stage again");
        let again = arm.call::<calls::ChatCompose>(&"second".to_owned());
        transcript.record("accepted", again);
        assert!(again, "{} would not stage a second line", arm.name());
    });
}

// OpenAC `ChatRouteParityTests.RecallingAnEarlierLineSendsItAgainTheSameOnBothClients`.
#[test]
fn recalling_an_earlier_line_sends_it_again_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("send three");
        for line in ["first", "second", "third"] {
            entry(arm, ChatEntryCommand::Submit(Some(line.to_owned())));
        }
        let _ = arm.outbound();

        transcript.step("recall the second");
        entry(arm, ChatEntryCommand::RecallPrevious);
        entry(arm, ChatEntryCommand::RecallPrevious);

        transcript.step("send it again");
        entry(arm, ChatEntryCommand::Submit(None));
        let sent = arm.record_outbound(transcript);
        assert_eq!(said(&one(&sent, TALK_ACTION)), "second", "{}", arm.name());
    });
}

// OpenAC `ChatRouteParityTests.AFeedSubscribedToLateReplaysTheSameOnBothClients`:
// a reader arriving late reads what the plugin surface keeps of the chat.
#[test]
fn a_feed_subscribed_to_late_replays_the_same_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("the server says three things");
        for index in 0..3 {
            arm.server
                .send(&wire::server_message(&format!("line {index}"), 0x01));
        }
        arm.sync();

        transcript.step("subscribe now and read it all back");
        let captured: Vec<String> = arm
            .call::<calls::ChatCaptured>(&0)
            .into_iter()
            .map(|l| l.text)
            .filter(|t| !t.contains(world::SYNC_MARKER))
            .collect();
        transcript.record("captured", &captured);
        assert!(
            ["line 0", "line 1", "line 2"]
                .iter()
                .all(|l| captured.iter().any(|c| c.contains(l))),
            "{}: {captured:?}",
            arm.name()
        );
    });
}

/// OpenAC `ChatRouteParityTests.AVerbTheClientRegistersIsClaimedOnBothClients`,
/// one case: nothing goes to the server, and the client answers.
fn a_verb_the_client_registers_is_claimed(line: &'static str) {
    run(move |arm, transcript| {
        let (told_from, feed_from) = (interface_text(arm).len(), arm.chat.len());
        transcript.step(&format!("type {line}"));
        let sent = type_line(arm, transcript, line);
        let told = record_interface_text(transcript, arm, told_from);
        assert!(sent.is_empty(), "{}: {line} went out: {sent:?}", arm.name());
        assert!(
            !told.is_empty() || !feed(arm, feed_from).is_empty(),
            "{} claimed {line} and said nothing about it",
            arm.name()
        );
    });
}

#[test]
fn the_status_verb_is_claimed_on_both_clients() {
    a_verb_the_client_registers_is_claimed("/status");
}

#[test]
fn the_nav_status_verb_is_claimed_on_both_clients() {
    a_verb_the_client_registers_is_claimed("/nav status");
}

/// `/nav grid`: claimed on both arms, and answered in the words each has.
/// The grid is drawn by a window, so the session with one shows it and the
/// one without says it has nothing to draw it on (OpenAC
/// `NavigationChatCommands`, the same two answers). OpenAC's windowed arm
/// has no graphics card and so gives the second answer too; here the
/// windowed arm really has a window attached, so the words are asserted per
/// arm and only the claim is compared.
#[test]
fn the_nav_grid_verb_is_claimed_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("type /nav grid");
        let from = arm.chat.len();
        entry(arm, ChatEntryCommand::Submit(Some("/nav grid".to_owned())));
        arm.sync();
        let answer = feed(arm, from);
        let sent = arm.record_outbound(transcript);
        transcript.record("feed.count", answer.len());
        assert!(
            sent.is_empty(),
            "{}: /nav grid went out: {sent:?}",
            arm.name()
        );
        let expected = if arm.windowed {
            "Navigation: grid shown"
        } else {
            "Navigation: this client has nothing to draw the grid on"
        };
        assert_eq!(answer, [expected], "{}", arm.name());
    });
}

#[test]
fn the_motor_verb_is_claimed_on_both_clients() {
    a_verb_the_client_registers_is_claimed("/motor turn left 90");
}

/// OpenAC `ChatRouteParityTests.AVerbNothingClaimsFallsThroughToSpeechOnBothClients`,
/// one case: the line goes out as speech, the slash written as the at-sign
/// the server expects, and nothing is said to the player instead.
fn a_verb_nothing_claims_falls_through_to_speech(line: &'static str, spoken: &'static str) {
    run(move |arm, transcript| {
        let told_from = interface_text(arm).len();
        transcript.step(&format!("type {line}"));
        let sent = type_line(arm, transcript, line);
        let told = record_interface_text(transcript, arm, told_from);
        assert_eq!(sent.len(), 1, "{}: {sent:?}", arm.name());
        assert_eq!(said(&one(&sent, TALK_ACTION)), spoken);
        assert!(told.is_empty(), "{}: {told:?}", arm.name());
    });
}

#[test]
fn an_unclaimed_verb_falls_through_to_speech_on_both_clients() {
    a_verb_nothing_claims_falls_through_to_speech("/noplugintookthis", "@noplugintookthis");
}

#[test]
fn an_unclaimed_verb_with_words_falls_through_to_speech_on_both_clients() {
    a_verb_nothing_claims_falls_through_to_speech(
        "/noplugintookthis with words",
        "@noplugintookthis with words",
    );
}
