//! Chat channels: the two channel families, the text class a numbered
//! channel's line gets, and the sentence it is shown as.
//!
//! The numbered ("legacy") channels are single bits: fellowship `0x800`,
//! vassals `0x1000`, patron `0x2000`, monarch `0x4000`, co-vassals
//! `0x0100_0000`, allegiance broadcast `0x0200_0000`, and so on. The room
//! channels (General, Trade, ...) come from the separate chat-room server
//! and are identified by a negotiated room id instead.

use super::LogTextType;

/// Fellowship channel.
pub const CHANNEL_FELLOW: u32 = 0x0000_0800;
/// Vassals channel (the player speaking to their vassals).
pub const CHANNEL_VASSALS: u32 = 0x0000_1000;
/// Patron channel (the player speaking to their patron).
pub const CHANNEL_PATRON: u32 = 0x0000_2000;
/// Monarch channel (the player speaking to their monarch).
pub const CHANNEL_MONARCH: u32 = 0x0000_4000;
/// Co-vassals channel.
pub const CHANNEL_CO_VASSALS: u32 = 0x0100_0000;
/// Allegiance broadcast channel.
pub const CHANNEL_ALLEGIANCE_BROADCAST: u32 = 0x0200_0000;
/// Fellowship broadcast channel.
pub const CHANNEL_FELLOW_BROADCAST: u32 = 0x0400_0000;

/// Which family a channel belongs to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ChatChannelSource {
    /// A numbered channel carried by the game server.
    Legacy,
    /// A room on the chat-room server.
    Turbine,
}

/// A chat channel the player can speak on.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub enum ChatChannelInfo {
    /// A numbered channel.
    Legacy {
        /// The channel bit.
        channel_id: u32,
        /// The name shown for it.
        display_name: String,
    },
    /// A chat room.
    Turbine {
        /// The negotiated room id.
        room_id: u32,
        /// The room's chat type.
        chat_type: u32,
        /// The room's dispatch type.
        dispatch_type: u32,
        /// The name shown for it.
        display_name: String,
    },
}

impl ChatChannelInfo {
    /// The name shown for the channel.
    #[must_use]
    pub fn display_name(&self) -> &str {
        match self {
            Self::Legacy { display_name, .. } | Self::Turbine { display_name, .. } => display_name,
        }
    }

    /// Which family the channel belongs to.
    #[must_use]
    pub fn source(&self) -> ChatChannelSource {
        match self {
            Self::Legacy { .. } => ChatChannelSource::Legacy,
            Self::Turbine { .. } => ChatChannelSource::Turbine,
        }
    }

    /// True when the server echoes the player's own message on this channel
    /// back to them, so the client must not print its own copy as well.
    /// Only the fellowship and allegiance-tree channels echo; chat rooms
    /// never do.
    #[must_use]
    pub fn is_self_echo_channel(&self) -> bool {
        match self {
            Self::Legacy { channel_id, .. } => matches!(
                *channel_id,
                CHANNEL_FELLOW
                    | CHANNEL_VASSALS
                    | CHANNEL_PATRON
                    | CHANNEL_MONARCH
                    | CHANNEL_CO_VASSALS
                    | CHANNEL_ALLEGIANCE_BROADCAST
            ),
            Self::Turbine { .. } => false,
        }
    }
}

/// The text class of a line on a numbered channel. `own_send` is true for
/// the player's own message.
///
/// Fellowship, co-vassals and allegiance broadcast use one class both ways;
/// patron, vassals and monarch are `Social` heard and `SocialSend` sent;
/// the fellowship broadcast is `Channel` heard and `Fellowship` sent; abuse
/// and help have their own classes; everything else is `Channel` heard and
/// `ChannelSend` sent.
#[must_use]
pub fn legacy_channel_chat_type(channel_id: u32, own_send: bool) -> LogTextType {
    match channel_id {
        0x0001 => LogTextType::Abuse,
        0x0400 => LogTextType::Help,
        CHANNEL_FELLOW => LogTextType::Fellowship,
        CHANNEL_VASSALS | CHANNEL_PATRON | CHANNEL_MONARCH => {
            if own_send {
                LogTextType::SocialSend
            } else {
                LogTextType::Social
            }
        }
        CHANNEL_CO_VASSALS | CHANNEL_ALLEGIANCE_BROADCAST => LogTextType::Social,
        CHANNEL_FELLOW_BROADCAST => {
            if own_send {
                LogTextType::Fellowship
            } else {
                LogTextType::Channel
            }
        }
        _ => {
            if own_send {
                LogTextType::ChannelSend
            } else {
                LogTextType::Channel
            }
        }
    }
}

/// The channel's name as the generic sentence uses it, or `None` for a
/// number that is no channel. The allegiance channels are named for who is
/// being spoken TO, which is what the sender's sentence needs.
#[must_use]
pub fn legacy_channel_name(channel_id: u32) -> Option<&'static str> {
    Some(match channel_id {
        0x0000_0001 => "Abuse",
        0x0000_0002 => "Admin",
        0x0000_0004 => "Audit",
        0x0000_0008 => "Advocate 1",
        0x0000_0010 => "Advocate 2",
        0x0000_0020 => "Advocate 3",
        0x0000_0200 => "Sentinel",
        0x0000_0400 => "Help",
        CHANNEL_FELLOW => "Fellowship",
        CHANNEL_VASSALS => "Vassals",
        CHANNEL_PATRON => "Patron",
        CHANNEL_MONARCH => "Monarch",
        CHANNEL_CO_VASSALS => "Co-vassals",
        CHANNEL_FELLOW_BROADCAST => "FellowBroadcast",
        0x0800_0000 => "Celestial Hand",
        0x1000_0000 => "Eldrytch Web",
        0x2000_0000 => "Radiant Blood",
        0x4000_0000 => "Olthoi",
        _ => return None,
    })
}

fn name_or_unknown(channel_id: u32) -> &'static str {
    legacy_channel_name(channel_id).unwrap_or("<unknown>")
}

/// The line for a message somebody else sent on a numbered channel. These
/// channels have no name on the wire: the wording comes from which channel
/// it is. `sender` is the speaker's name, already decorated for display.
#[must_use]
pub fn legacy_channel_heard(channel_id: u32, sender: &str, text: &str) -> String {
    match channel_id {
        CHANNEL_FELLOW => format!("[Fellowship] {sender} says, \"{text}\""),
        CHANNEL_VASSALS => format!("Your patron {sender} says to you, \"{text}\""),
        CHANNEL_PATRON => format!("Your vassal {sender} says to you, \"{text}\""),
        CHANNEL_MONARCH => format!("Your follower {sender} says to you, \"{text}\""),
        CHANNEL_CO_VASSALS => format!("[Co-Vassals] {sender} says, \"{text}\""),
        CHANNEL_ALLEGIANCE_BROADCAST => {
            format!("[Allegiance Broadcast] {sender} says, \"{text}\"")
        }
        _ => format!(
            "{sender} says on the {} channel, \"{text}\"",
            name_or_unknown(channel_id)
        ),
    }
}

/// The line for a message the player sent on a numbered channel.
#[must_use]
pub fn legacy_channel_sent(channel_id: u32, text: &str) -> String {
    match channel_id {
        CHANNEL_FELLOW => format!("[Fellowship] You say, \"{text}\""),
        CHANNEL_VASSALS | CHANNEL_PATRON | CHANNEL_MONARCH => {
            format!(
                "You say to your {}, \"{text}\"",
                name_or_unknown(channel_id)
            )
        }
        CHANNEL_CO_VASSALS => format!("[Co-Vassals] You say, \"{text}\""),
        CHANNEL_ALLEGIANCE_BROADCAST => format!("[Allegiance Broadcast] You say, \"{text}\""),
        // A broadcast to the fellowship is shown as it was written.
        CHANNEL_FELLOW_BROADCAST => text.to_owned(),
        _ => format!(
            "You say on the {} channel, \"{text}\"",
            name_or_unknown(channel_id)
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn legacy(channel_id: u32, name: &str) -> ChatChannelInfo {
        ChatChannelInfo::Legacy {
            channel_id,
            display_name: name.to_owned(),
        }
    }

    // OpenAC tests/AcDream.Core.Tests/Chat/ChatChannelInfoTests.cs
    #[test]
    fn legacy_fellowship_bitflag_is_self_echo() {
        let c = legacy(0x800, "Fellowship");
        assert_eq!(c.source(), ChatChannelSource::Legacy);
        assert_eq!(c.display_name(), "Fellowship");
        assert!(c.is_self_echo_channel());
    }

    #[test]
    fn legacy_allegiance_tree_bitflags_are_self_echo() {
        for id in [0x1000, 0x2000, 0x4000, 0x0100_0000] {
            assert!(
                legacy(id, "Allegiance").is_self_echo_channel(),
                "channel {id:#010X} should self-echo"
            );
        }
    }

    #[test]
    fn legacy_allegiance_broadcast_bitflag_is_self_echo() {
        assert!(legacy(0x0200_0000, "AllegianceBroadcast").is_self_echo_channel());
    }

    #[test]
    fn turbine_is_never_self_echo() {
        let t = ChatChannelInfo::Turbine {
            room_id: 0x1234_5678,
            chat_type: 2,
            dispatch_type: 2,
            display_name: "General".to_owned(),
        };
        assert_eq!(t.source(), ChatChannelSource::Turbine);
        assert_eq!(t.display_name(), "General");
        assert!(!t.is_self_echo_channel());
    }

    #[test]
    fn legacy_unknown_bitflag_is_not_self_echo() {
        assert!(!legacy(0xCAFE_F00D, "WatCha").is_self_echo_channel());
    }

    // OpenAC tests/AcDream.Core.Tests/Chat/LegacyChannelChatTypeTests.cs
    #[test]
    fn chat_type_same_for_hear_and_send() {
        for (channel, expected) in [(0x0800, 0x13), (0x0100_0000, 0x0A), (0x0200_0000, 0x0A)] {
            assert_eq!(legacy_channel_chat_type(channel, false).as_u32(), expected);
            assert_eq!(legacy_channel_chat_type(channel, true).as_u32(), expected);
        }
    }

    #[test]
    fn chat_type_hear_is_social_send_is_social_send() {
        for channel in [0x1000, 0x2000, 0x4000] {
            assert_eq!(legacy_channel_chat_type(channel, false).as_u32(), 0x0A);
            assert_eq!(legacy_channel_chat_type(channel, true).as_u32(), 0x0B);
        }
    }

    #[test]
    fn chat_type_abuse_is_the_only_0x0e_producer() {
        assert_eq!(legacy_channel_chat_type(0x0001, false).as_u32(), 0x0E);
        assert_eq!(legacy_channel_chat_type(0x0001, true).as_u32(), 0x0E);
    }

    #[test]
    fn chat_type_help_is_same_for_hear_and_send() {
        assert_eq!(legacy_channel_chat_type(0x0400, false).as_u32(), 0x0F);
        assert_eq!(legacy_channel_chat_type(0x0400, true).as_u32(), 0x0F);
    }

    #[test]
    fn chat_type_fellow_broadcast_hear_is_channel_send_is_fellowship() {
        assert_eq!(legacy_channel_chat_type(0x0400_0000, false).as_u32(), 0x08);
        assert_eq!(legacy_channel_chat_type(0x0400_0000, true).as_u32(), 0x13);
    }

    #[test]
    fn chat_type_catch_all_hear_is_channel_send_is_channel_send() {
        for channel in [0x0100, 0xDEAD] {
            assert_eq!(legacy_channel_chat_type(channel, false).as_u32(), 0x08);
            assert_eq!(legacy_channel_chat_type(channel, true).as_u32(), 0x09);
        }
    }

    // OpenAC tests/AcDream.Core.Tests/Chat/LegacyChannelSentenceTests.cs
    #[test]
    fn what_is_heard_is_worded_by_the_channel() {
        let cases: [(u32, &str); 9] = [
            (0x0800, "[Fellowship] Bob says, \"hi\""),
            (0x1000, "Your patron Bob says to you, \"hi\""),
            (0x2000, "Your vassal Bob says to you, \"hi\""),
            (0x4000, "Your follower Bob says to you, \"hi\""),
            (0x0100_0000, "[Co-Vassals] Bob says, \"hi\""),
            (0x0200_0000, "[Allegiance Broadcast] Bob says, \"hi\""),
            (0x0400, "Bob says on the Help channel, \"hi\""),
            (
                0x0800_0000,
                "Bob says on the Celestial Hand channel, \"hi\"",
            ),
            (7, "Bob says on the <unknown> channel, \"hi\""),
        ];
        for (channel, expected) in cases {
            assert_eq!(legacy_channel_heard(channel, "Bob", "hi"), expected);
        }
    }

    #[test]
    fn what_is_sent_is_worded_by_the_channel() {
        let cases: [(u32, &str); 8] = [
            (0x0800, "[Fellowship] You say, \"hi\""),
            (0x1000, "You say to your Vassals, \"hi\""),
            (0x2000, "You say to your Patron, \"hi\""),
            (0x4000, "You say to your Monarch, \"hi\""),
            (0x0100_0000, "[Co-Vassals] You say, \"hi\""),
            (0x0200_0000, "[Allegiance Broadcast] You say, \"hi\""),
            (0x0400_0000, "hi"),
            (0x0400, "You say on the Help channel, \"hi\""),
        ];
        for (channel, expected) in cases {
            assert_eq!(legacy_channel_sent(channel, "hi"), expected);
        }
    }
}
