//! What a plugin hears of a chat line (OpenAC
//! `Scenarios/PluginChatLineParityTests.cs`): a channel line's channel and
//! printed line, a tell's, a tell to oneself as a thought, a shout as
//! speech, and a speakerless fellowship broadcast.

use ac_plugin::abi::{ChatLine, Event};

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::{wire, world};

const PATRON_CHANNEL: u32 = 0x1000;
const SPEAKER: u32 = 0x5000_0077;

/// The one chat line the relay heard since the last reading.
fn the_line(arm: &mut Arm) -> ChatLine {
    let lines: Vec<ChatLine> = arm
        .events()
        .into_iter()
        .filter_map(|e| match e {
            Event::ChatReceived(line) => Some(line),
            _ => None,
        })
        .collect();
    assert_eq!(lines.len(), 1, "{}: {lines:?}", arm.name());
    lines.into_iter().next().unwrap()
}

// OpenAC `PluginChatLineParityTests.AChannelLineTellsAPluginItsChannelAndPrintedLineOnBothClients`.
#[test]
fn a_channel_line_tells_a_plugin_its_channel_and_printed_line_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("a patron speaks on the channel");
        arm.server.game_event(
            world::PLAYER,
            0x0147,
            &wire::channel_broadcast(PATRON_CHANNEL, "Bob", "hi"),
        );
        arm.sync();
        let line = the_line(arm);
        transcript.record_id("channel", line.channel_id);
        transcript.record("printed", &line.display_text);
        assert_eq!(line.channel_id, PATRON_CHANNEL);
        assert_eq!(
            line.display_text,
            "Your patron <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hi\""
        );
    });
}

// OpenAC `PluginChatLineParityTests.ATellCarriesNoChannelAndItsPrintedLineOnBothClients`.
#[test]
fn a_tell_carries_no_channel_and_its_printed_line_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("someone tells the character something");
        arm.server.game_event(
            world::PLAYER,
            0x02BD,
            &wire::tell("hello", "Bob", SPEAKER, world::PLAYER, 0x03),
        );
        arm.sync();
        let line = the_line(arm);
        transcript.record_id("channel", line.channel_id);
        transcript.record("printed", &line.display_text);
        assert_eq!(line.channel_id, 0);
        assert_eq!(
            line.display_text,
            format!("<Tell:IIDString:{SPEAKER}:Bob>Bob<\\Tell> tells you, \"hello\"")
        );
    });
}

// OpenAC `PluginChatLineParityTests.ATellToOneselfReadsAsAThoughtOnBothClients`.
#[test]
fn a_tell_to_oneself_reads_as_a_thought_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("the character tells itself something");
        arm.server.game_event(
            world::PLAYER,
            0x02BD,
            &wire::tell(
                "remember the key",
                "Acdream",
                world::PLAYER,
                world::PLAYER,
                0x03,
            ),
        );
        arm.sync();
        let line = the_line(arm);
        transcript.record("printed", &line.display_text);
        assert_eq!(line.display_text, "You think, \"remember the key\"");
    });
}

// OpenAC `PluginChatLineParityTests.AShoutPrintsAsSpeechOnBothClients`.
#[test]
fn a_shout_prints_as_speech_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("a guard shouts");
        arm.server.send(&wire::ranged_speech(
            "Guard",
            "Halt!",
            0x8000_1234,
            0x02,
            60.0,
        ));
        arm.sync();
        let line = the_line(arm);
        transcript.record("printed", &line.display_text);
        assert_eq!(line.display_text, "Guard says, \"Halt!\"");
    });
}

// OpenAC `PluginChatLineParityTests.ASpeakerlessFellowshipBroadcastIsAFellowshipLineOnBothClients`.
#[test]
fn a_speakerless_fellowship_broadcast_is_a_fellowship_line_on_both_clients() {
    run(|arm, transcript| {
        transcript.step("the fellowship is told something");
        arm.server.game_event(
            world::PLAYER,
            0x0147,
            &wire::channel_broadcast(0x0400_0000, "", "Bob has joined the fellowship."),
        );
        arm.sync();
        let line = the_line(arm);
        transcript.record("type", line.log_text_type);
        transcript.record("printed", &line.display_text);
        assert_eq!(line.log_text_type, 0x13);
        assert_eq!(line.display_text, "Bob has joined the fellowship.");
    });
}
