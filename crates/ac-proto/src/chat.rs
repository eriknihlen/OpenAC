//! Chat: speech heard nearby, emotes, server text, tells and channel
//! traffic, the error and notice events that print into the chat window,
//! and the client's own talk, tell, channel and emote requests.
//!
//! Chat types (`chat_type`) are kept as the raw `u32` the server sends; the
//! chat log maps them to its text types.

use ac_bits::Reader;

use crate::wire::{action, read_count, read_string, string_action};
use crate::{GameActionType, Opcode, ProtoError, Result};

/// Speech heard from someone nearby ([`Opcode::HearSpeech`]) or from
/// someone whose voice carries a set distance
/// ([`Opcode::HearRangedSpeech`]).
#[derive(Debug, Clone, PartialEq)]
pub struct HearSpeech {
    /// What was said.
    pub text: String,
    /// Who said it.
    pub sender_name: String,
    /// The speaker's object id.
    pub sender_guid: u32,
    /// How far the voice carries, meters; `None` for local speech.
    pub range: Option<f32>,
    /// Chat type of the line.
    pub chat_type: u32,
}

impl HearSpeech {
    /// Parse either speech message: opcode, text, sender name, sender id,
    /// the range (ranged speech only), chat type.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        let opcode = r.u32()?;
        let ranged = match opcode {
            o if o == Opcode::HearSpeech.value() => false,
            o if o == Opcode::HearRangedSpeech.value() => true,
            actual => {
                return Err(ProtoError::WrongOpcode {
                    expected: Opcode::HearSpeech.value(),
                    actual,
                });
            }
        };
        let text = read_string(&mut r)?;
        let sender_name = read_string(&mut r)?;
        let sender_guid = r.u32()?;
        let range = if ranged { Some(r.f32()?) } else { None };
        Ok(Self {
            text,
            sender_name,
            sender_guid,
            range,
            chat_type: r.u32()?,
        })
    }

    /// True for ranged speech.
    #[must_use]
    pub fn is_ranged(&self) -> bool {
        self.range.is_some()
    }
}

/// An emote line: "Caith waves at you" ([`Opcode::EmoteText`]), or the same
/// with an animation ([`Opcode::SoulEmote`]). Both have one layout.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Emote {
    /// The emoting object.
    pub sender_guid: u32,
    /// Its name.
    pub sender_name: String,
    /// The emote text.
    pub text: String,
}

impl Emote {
    /// Parse an [`Opcode::EmoteText`] body: opcode, sender id, sender
    /// name, text.
    pub fn parse_emote_text(body: &[u8]) -> Result<Self> {
        Self::parse_with(body, Opcode::EmoteText)
    }

    /// Parse an [`Opcode::SoulEmote`] body; same layout as
    /// [`Emote::parse_emote_text`].
    pub fn parse_soul_emote(body: &[u8]) -> Result<Self> {
        Self::parse_with(body, Opcode::SoulEmote)
    }

    fn parse_with(body: &[u8], opcode: Opcode) -> Result<Self> {
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, opcode)?;
        Ok(Self {
            sender_guid: r.u32()?,
            sender_name: read_string(&mut r)?,
            text: read_string(&mut r)?,
        })
    }
}

/// Server text for the chat window ([`Opcode::ServerMessage`]): command
/// replies, shutdown warnings. The text may hold several lines.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerMessage {
    /// The text.
    pub message: String,
    /// Chat type of the line.
    pub chat_type: u32,
}

impl ServerMessage {
    /// Parse the body: opcode, text, chat type.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, Opcode::ServerMessage)?;
        Ok(Self {
            message: read_string(&mut r)?,
            chat_type: r.u32()?,
        })
    }
}

/// A tell to the player (game event [`crate::GameEventType::TELL`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Tell {
    /// What was said.
    pub message: String,
    /// Who said it.
    pub sender_name: String,
    /// The sender's id.
    pub sender_guid: u32,
    /// The recipient's id.
    pub target_guid: u32,
    /// Chat type of the line.
    pub chat_type: u32,
}

impl Tell {
    /// Parse the event payload: message, sender name, sender id, target
    /// id, chat type.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            message: read_string(&mut r)?,
            sender_name: read_string(&mut r)?,
            sender_guid: r.u32()?,
            target_guid: r.u32()?,
            chat_type: r.u32()?,
        })
    }
}

/// A line on a chat channel such as fellowship or allegiance (game event
/// [`crate::GameEventType::CHANNEL_BROADCAST`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChannelBroadcast {
    /// The channel bit.
    pub channel_id: u32,
    /// Who said it.
    pub sender_name: String,
    /// What was said.
    pub message: String,
}

impl ChannelBroadcast {
    /// Parse the event payload: channel, sender name, message.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            channel_id: r.u32()?,
            sender_name: read_string(&mut r)?,
            message: read_string(&mut r)?,
        })
    }
}

/// The single string of a text-only event: a transient notice
/// ([`crate::GameEventType::COMMUNICATION_TRANSIENT_STRING`]), a popup
/// ([`crate::GameEventType::POPUP_STRING`]), a death message
/// ([`crate::GameEventType::VICTIM_NOTIFICATION`],
/// [`crate::GameEventType::KILLER_NOTIFICATION`]) or an evasion notice
/// ([`crate::GameEventType::EVASION_ATTACKER_NOTIFICATION`],
/// [`crate::GameEventType::EVASION_DEFENDER_NOTIFICATION`]).
pub fn parse_string_event(payload: &[u8]) -> Result<String> {
    read_string(&mut Reader::new(payload))
}

/// An error code event ([`crate::GameEventType::WEENIE_ERROR`]): the code
/// whose text the chat log prints.
pub fn parse_weenie_error(payload: &[u8]) -> Result<u32> {
    Ok(Reader::new(payload).u32()?)
}

/// An error code with a name or value to put in its text
/// ([`crate::GameEventType::WEENIE_ERROR_WITH_STRING`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WeenieErrorWithString {
    /// The error code.
    pub error_code: u32,
    /// The text interpolated into the message.
    pub interpolation: String,
}

impl WeenieErrorWithString {
    /// Parse the event payload: code, string.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            error_code: r.u32()?,
            interpolation: read_string(&mut r)?,
        })
    }
}

/// The answer to an age query ([`crate::GameEventType::QUERY_AGE_RESPONSE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QueryAgeResponse {
    /// Whose age.
    pub name: String,
    /// The age, already worded by the server.
    pub age: String,
}

impl QueryAgeResponse {
    /// Parse the event payload: name, age.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            name: read_string(&mut r)?,
            age: read_string(&mut r)?,
        })
    }
}

/// A list of names: the available channels
/// ([`crate::GameEventType::CHANNEL_INDEX`]) or who listens on a channel
/// ([`crate::GameEventType::CHANNEL_LIST`]). Payload: count, then the
/// strings.
pub fn parse_name_list(payload: &[u8]) -> Result<Vec<String>> {
    let mut r = Reader::new(payload);
    // Each string takes at least its two-byte length.
    let count = read_count(&mut r, 2, "names")?;
    (0..count).map(|_| read_string(&mut r)).collect()
}

/// Say `message` to everyone nearby. Game action, UI queue.
pub fn build_talk(sequence: u32, message: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::TALK, message)
}

/// Tell `target_name` `message`. The message comes first on the wire, then
/// the name. Game action, UI queue.
pub fn build_tell(sequence: u32, target_name: &str, message: &str) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::TELL);
    w.string16l(message)?;
    w.string16l(target_name)?;
    Ok(w.into_vec())
}

/// Speak `message` to one object by id, which also reaches creatures and
/// non-player characters a tell by name cannot. Message first, then the
/// id. Game action, UI queue.
pub fn build_talk_direct(sequence: u32, target_guid: u32, message: &str) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::TALK_DIRECT);
    w.string16l(message)?;
    w.u32(target_guid);
    Ok(w.into_vec())
}

/// Say `message` on a chat channel (fellowship, allegiance, ...). Channel
/// first, then the message. Game action, UI queue.
pub fn build_chat_channel(sequence: u32, channel_id: u32, message: &str) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::CHAT_CHANNEL);
    w.u32(channel_id);
    w.string16l(message)?;
    Ok(w.into_vec())
}

/// Emote `message`. Game action, UI queue.
pub fn build_emote(sequence: u32, message: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::EMOTE, message)
}

/// Emote `message` with an animation. Game action, UI queue.
pub fn build_soul_emote(sequence: u32, message: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::SOUL_EMOTE, message)
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `HearSpeechGoldenTests.cs`, `EmoteTextTests.cs`, `SoulEmoteTests.cs`,
    //! `ServerMessageTests.cs`, `ChatTests.cs`, the chat parts of
    //! `GameEventDispatcherTests.cs` and `ClientCommandResponsesTests.cs`
    //! (channel lists), and `ClientCommandRequestsTests.cs` (emotes).

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    const SPEECH: u32 = 0x0B;

    fn speech(opcode: u32, text: &str, sender: &str, guid: u32, range: Option<f32>) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(opcode);
        w.string16l(text).unwrap();
        w.string16l(sender).unwrap();
        w.u32(guid);
        if let Some(range) = range {
            w.f32(range);
        }
        w.u32(SPEECH);
        w.into_vec()
    }

    #[test]
    fn local_speech_decodes_every_field() {
        // Empty strings and lengths 1..4 cover every padding residue.
        for (text, sender, guid) in [
            ("Hello, Dereth!", "Barris", 0x5000_0001),
            ("", "", 0),
            ("a", "bb", 0x7C95_B01A),
            ("ccc", "dddd", 0x8001_14C0),
            ("Café time", "Seán", 0xA9B4_0001),
        ] {
            let parsed = HearSpeech::parse(&speech(0x02BB, text, sender, guid, None)).unwrap();
            assert_eq!(parsed.text, text);
            assert_eq!(parsed.sender_name, sender);
            assert_eq!(parsed.sender_guid, guid);
            assert_eq!(parsed.chat_type, SPEECH);
            assert!(!parsed.is_ranged());
        }
    }

    #[test]
    fn ranged_speech_reads_range_before_chat_type() {
        for (text, sender, range) in [("HELP!", "Barris", 60.0), ("", "", 0.0), ("a", "bb", 12.5)] {
            let parsed =
                HearSpeech::parse(&speech(0x02BC, text, sender, 0x5000_0001, Some(range))).unwrap();
            assert_eq!(parsed.text, text);
            assert_eq!(parsed.range, Some(range));
            // The chat type is not the range's bits (60.0 is 0x42700000).
            assert_eq!(parsed.chat_type, SPEECH);
        }
    }

    #[test]
    fn ranged_speech_without_its_range_is_an_error() {
        // Local layout under the ranged opcode: the chat type is read as the
        // range and the chat type itself is missing.
        let mut body = speech(0x02BB, "HELP!", "Barris", 0x5000_0001, None);
        body[..4].copy_from_slice(&0x02BCu32.to_le_bytes());
        assert!(HearSpeech::parse(&body).is_err());
    }

    #[test]
    fn speech_rejects_other_opcodes_and_truncation() {
        let mut w = Writer::new();
        w.u32(0x02BD);
        w.string16l("x").unwrap();
        w.string16l("y").unwrap();
        w.u32(1);
        w.u32(1);
        assert!(matches!(
            HearSpeech::parse(w.as_slice()),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert_every_prefix_fails(
            &speech(0x02BB, "Hello", "Barris", 1, None),
            HearSpeech::parse,
        );
        assert_every_prefix_fails(
            &speech(0x02BC, "Hello", "Barris", 1, Some(2.0)),
            HearSpeech::parse,
        );
    }

    fn emote(opcode: u32, guid: u32, sender: &str, text: &str) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(opcode);
        w.u32(guid);
        w.string16l(sender).unwrap();
        w.string16l(text).unwrap();
        w.into_vec()
    }

    #[test]
    fn emote_text_and_soul_emote_round_trip() {
        let parsed =
            Emote::parse_emote_text(&emote(0x01E0, 0xDEAD_BEEF, "Caith", "waves at you")).unwrap();
        assert_eq!(
            parsed,
            Emote {
                sender_guid: 0xDEAD_BEEF,
                sender_name: "Caith".into(),
                text: "waves at you".into()
            }
        );
        let parsed =
            Emote::parse_emote_text(&emote(0x01E0, 1, "Élise", "waves at you, Café")).unwrap();
        assert_eq!(parsed.sender_name, "Élise");
        assert_eq!(parsed.text, "waves at you, Café");
        let soul = Emote::parse_soul_emote(&emote(0x01E2, 0xCAFE_F00D, "Caith", "dances")).unwrap();
        assert_eq!(soul.text, "dances");
        assert!(Emote::parse_soul_emote(&emote(0x01E0, 1, "a", "b")).is_err());
        assert!(Emote::parse_emote_text(&words(&[0x1234_5678, 0])).is_err());
        // "Caith" (5) pads; "ab" ends the body without padding.
        assert_every_prefix_fails(&emote(0x01E0, 1, "Caith", "ab"), Emote::parse_emote_text);
    }

    fn server_message(text: &str, chat_type: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xF7E0);
        w.string16l(text).unwrap();
        w.u32(chat_type);
        w.into_vec()
    }

    #[test]
    fn server_message_round_trips_and_keeps_newlines() {
        let parsed =
            ServerMessage::parse(&server_message("The server will reset in 5 minutes.", 5))
                .unwrap();
        assert_eq!(parsed.message, "The server will reset in 5 minutes.");
        assert_eq!(parsed.chat_type, 5);
        let text = "@acecommands\n@help\n@teleport";
        assert_eq!(
            ServerMessage::parse(&server_message(text, 0))
                .unwrap()
                .message,
            text
        );
        assert!(ServerMessage::parse(&words(&[0xDEAD_BEEF, 0])).is_err());
        assert_every_prefix_fails(&server_message("hi", 0), ServerMessage::parse);
    }

    #[test]
    fn tell_and_channel_broadcast_round_trip() {
        let mut w = Writer::new();
        w.string16l("hi").unwrap();
        w.string16l("Alice").unwrap();
        w.u32(0xAA);
        w.u32(0xBB);
        w.u32(1);
        let tell = Tell::parse(w.as_slice()).unwrap();
        assert_eq!(
            tell,
            Tell {
                message: "hi".into(),
                sender_name: "Alice".into(),
                sender_guid: 0xAA,
                target_guid: 0xBB,
                chat_type: 1
            }
        );
        assert_every_prefix_fails(w.as_slice(), Tell::parse);

        let mut w = Writer::new();
        w.u32(42);
        w.string16l("Hero").unwrap();
        w.string16l("hello allegiance").unwrap();
        let broadcast = ChannelBroadcast::parse(w.as_slice()).unwrap();
        assert_eq!(broadcast.channel_id, 42);
        assert_eq!(broadcast.sender_name, "Hero");
        assert_eq!(broadcast.message, "hello allegiance");
        // A final string of 14 bytes needs no padding, so every cut fails.
        let mut w = Writer::new();
        w.u32(42);
        w.string16l("Hero").unwrap();
        w.string16l("hi allegiance!").unwrap();
        assert_every_prefix_fails(w.as_slice(), ChannelBroadcast::parse);
    }

    #[test]
    fn string_events_keep_every_padding_residue() {
        for text in ["", "a", "bb", "ccc", "dddd", "Your spell fizzled!"] {
            let mut w = Writer::new();
            w.string16l(text).unwrap();
            assert_eq!(parse_string_event(w.as_slice()).as_deref(), Ok(text));
        }
        assert!(parse_string_event(&[3, 0, b'a']).is_err());
    }

    #[test]
    fn error_and_age_events() {
        assert_eq!(parse_weenie_error(&words(&[42])), Ok(42));
        assert!(parse_weenie_error(&[1, 2, 3]).is_err());
        let mut w = Writer::new();
        w.u32(0x051E);
        w.string16l("Bob").unwrap();
        let parsed = WeenieErrorWithString::parse(w.as_slice()).unwrap();
        assert_eq!(parsed.error_code, 0x051E);
        assert_eq!(parsed.interpolation, "Bob");
        let mut w = Writer::new();
        w.string16l("Alice").unwrap();
        w.string16l("2 days").unwrap();
        let age = QueryAgeResponse::parse(w.as_slice()).unwrap();
        assert_eq!(age.name, "Alice");
        assert_eq!(age.age, "2 days");
        assert_every_prefix_fails(w.as_slice(), QueryAgeResponse::parse);
    }

    #[test]
    fn name_lists_round_trip() {
        let mut w = Writer::new();
        w.u32(2);
        w.string16l("Admin").unwrap();
        w.string16l("Help").unwrap();
        assert_eq!(
            parse_name_list(w.as_slice()),
            Ok(vec!["Admin".to_string(), "Help".to_string()])
        );
        assert_eq!(parse_name_list(&words(&[0])), Ok(vec![]));
        let mut w = Writer::new();
        w.u32(3);
        for name in ["Caith", "Vandal", "Elysia"] {
            w.string16l(name).unwrap();
        }
        assert_eq!(parse_name_list(w.as_slice()).unwrap().len(), 3);
        assert!(matches!(
            parse_name_list(&words(&[1000])),
            Err(ProtoError::CountExceedsPayload { .. })
        ));
    }

    #[test]
    fn talk_packs_the_string_after_the_header() {
        let body = build_talk(3, "hi").unwrap();
        assert_eq!(body[..12], words(&[0xF7B1, 3, 0x15]));
        assert_eq!(body[12..], [2, 0, b'h', b'i']);
        // 2 + 1 = 3 bytes, padded to 4.
        assert_eq!(build_talk(3, "h").unwrap().len(), 16);
    }

    #[test]
    fn tell_writes_message_first_then_target() {
        let body = build_tell(5, "Alice", "hey").unwrap();
        assert_eq!(body[8..12], 0x5Du32.to_le_bytes());
        assert_eq!(body[12..20], [3, 0, b'h', b'e', b'y', 0, 0, 0]);
        assert_eq!(body[20..], [5, 0, b'A', b'l', b'i', b'c', b'e', 0]);
    }

    #[test]
    fn talk_direct_writes_message_then_target_id() {
        let body = build_talk_direct(7, 0x8000_ABCD, "hey").unwrap();
        assert_eq!(body[..12], words(&[0xF7B1, 7, 0x32]));
        assert_eq!(body[12..17], [3, 0, b'h', b'e', b'y']);
        assert_eq!(body[20..], 0x8000_ABCDu32.to_le_bytes());
        assert_eq!(body.len(), 24);
        // No padding: the id follows the record directly.
        let body = build_talk_direct(2, 0x1234_5678, "hi").unwrap();
        assert_eq!(body.len(), 20);
        assert_eq!(body[16..], 0x1234_5678u32.to_le_bytes());
        let body = build_talk_direct(1, 0x5000_0001, "abcd").unwrap();
        assert_eq!(body.len(), 24);
        assert_eq!(body[18..20], [0, 0]);
    }

    #[test]
    fn chat_channel_writes_channel_before_message() {
        let body = build_chat_channel(1, 42, "tell me the good dungeons").unwrap();
        assert_eq!(body[..16], words(&[0xF7B1, 1, 0x147, 42]));
        assert_eq!(body[16..18], [25, 0]);
    }

    #[test]
    fn emote_builders_pack_one_windows_1252_string() {
        for (body, action) in [
            (build_emote(11, "Bjørn").unwrap(), 0x01DF),
            (build_soul_emote(11, "Bjørn").unwrap(), 0x01E1),
        ] {
            assert_eq!(body.len(), 20);
            assert_eq!(body[..12], words(&[0xF7B1, 11, action]));
            assert_eq!(body[12..], [5, 0, 0x42, 0x6A, 0xF8, 0x72, 0x6E, 0]);
        }
    }
}
