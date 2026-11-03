//! The records and events that cross the boundary, one encoding each.

use crate::wire::{DecodeError, Reader, Shape, Wire, Writer};
use crate::{wire_record, wire_union};

/// How serious a log line is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub enum LogLevel {
    /// Detail for whoever debugs the plugin.
    Debug,
    /// Normal progress.
    #[default]
    Info,
    /// Something is off but the plugin goes on.
    Warn,
    /// Something failed.
    Error,
}

impl LogLevel {
    /// The level's word in log lines.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::Debug => "debug",
            Self::Info => "info",
            Self::Warn => "warn",
            Self::Error => "error",
        }
    }
}

impl Wire for LogLevel {
    // Written by hand like the encoding below it: change them together.
    const SHAPE: u64 = Shape::new("enum")
        .tag(0)
        .name("Debug")
        .tag(1)
        .name("Info")
        .tag(2)
        .name("Warn")
        .tag(3)
        .name("Error")
        .finish();
    fn encode(&self, w: &mut Writer) {
        let tag: u8 = match self {
            Self::Debug => 0,
            Self::Info => 1,
            Self::Warn => 2,
            Self::Error => 3,
        };
        tag.encode(w);
    }
    fn read(r: &mut Reader<'_>) -> Self {
        match u8::read(r) {
            0 => Self::Debug,
            1 => Self::Info,
            2 => Self::Warn,
            3 => Self::Error,
            tag => {
                r.fail(DecodeError::UnknownTag {
                    what: "log level",
                    tag: u32::from(tag),
                });
                Self::Debug
            }
        }
    }
}

wire_record! {
    /// A log line ([`crate::Import::HostLog`]).
    pub struct LogRequest {
        /// How serious.
        pub level: LogLevel,
        /// The text.
        pub message: String,
    }
}

wire_record! {
    /// What a plugin is told when it starts.
    pub struct StartInfo {
        /// Its id, from the manifest.
        pub plugin_id: String,
        /// The API version the client provides (`MAJOR.MINOR`).
        pub api_version: String,
        /// The client's own version (`MAJOR.MINOR.PATCH`).
        pub client_version: String,
    }
}

wire_record! {
    /// A chat verb a plugin registers ([`crate::Import::CommandsRegister`]).
    pub struct CommandDef {
        /// The verb, without `/` (1 to 32 letters or digits).
        pub verb: String,
        /// Other names for it.
        pub aliases: Vec<String>,
        /// One line for the help list.
        pub description: String,
    }
}

wire_record! {
    /// A registered verb was typed.
    pub struct CommandCall {
        /// The verb as registered (not the alias typed).
        pub verb: String,
        /// Everything after the verb, trimmed.
        pub arguments: String,
        /// The whole line as typed, trimmed.
        pub raw: String,
    }
}

wire_record! {
    /// A completion offered for a partly typed command
    /// ([`crate::Import::CommandsComplete`]; OpenAC
    /// `PluginCommandCompletion`).
    pub struct CommandCompletion {
        /// The text the completion would put in place of what was typed.
        pub text: String,
        /// A short explanation shown beside it, if any.
        pub description: Option<String>,
    }
}

wire_record! {
    /// A line to show in the chat transcript ([`crate::Import::ChatPost`]).
    pub struct ChatPost {
        /// The text.
        pub text: String,
        /// The text class it is coloured and filtered by (retail
        /// `LogTextType`; 0 is the default class).
        pub log_text_type: u32,
    }
}

/// The [`ChatLine::kind`] of a short status notice shown over the world
/// rather than written into the transcript: only chat filters see one
/// (OpenAC `PluginChatMessage.StatusTextKind`).
pub const STATUS_TEXT_KIND: u32 = 100;

wire_record! {
    /// One line of chat as a plugin sees it.
    pub struct ChatLine {
        /// The client's counter for the line (0 for a line a filter is
        /// asked about: it is not kept yet).
        pub sequence: u64,
        /// The speaker's object id, 0 without one.
        pub sender_id: u32,
        /// The line kind: 0 local speech, 1 ranged speech, 2 channel, 3 tell,
        /// 4 system, 5 popup, 6 emote, 7 soul emote, 8 combat;
        /// [`STATUS_TEXT_KIND`] for a status notice a filter is asked about.
        pub kind: u32,
        /// The speaker's name.
        pub sender: String,
        /// The line's words as they arrived: no speaker, verb, quotes or
        /// timestamp (OpenAC `Text`). A filter sees them before the
        /// language filter; every reader after it, as printed.
        pub text: String,
        /// The channel's name, when it has one.
        pub channel: String,
        /// The text class (retail `LogTextType`).
        pub log_text_type: u32,
        /// For a combat line: 1 outgoing, 2 incoming, 3 a failure; else 0.
        pub combat_kind: u32,
        /// When it arrived, UTC milliseconds since the Unix epoch.
        pub received_unix_ms: i64,
        /// The number of the channel a channel line (kind 2) came on, 0 on
        /// every other line. The fixed channels have no name and are told
        /// apart by this alone: 0x800 fellowship, 0x1000 from a patron,
        /// 0x2000 from a vassal, 0x4000 from a follower, 0x100_0000
        /// co-vassals, 0x200_0000 allegiance broadcast, 0x400_0000
        /// fellowship broadcast, 0x8, 0x10 and 0x20 the advocate channels.
        pub channel_id: u32,
        /// The whole line as the client's chat window prints it: channel
        /// prefix, speaker, verb and quoted words, another player's name
        /// wrapped in the tell link (`<Tell:IIDString:id:Name>Name<\Tell>`);
        /// no timestamp, no closing line break (OpenAC `DisplayText`).
        pub display_text: String,
    }
}

wire_record! {
    /// A link clicked in the chat transcript.
    pub struct ChatLinkClicked {
        /// What it links: 0 a map coordinate.
        pub kind: u8,
        /// The text shown for the link.
        pub display_text: String,
        /// A coordinate link's (east-west, north-south) map coordinates,
        /// west and south negative.
        pub coordinate: Option<(f64, f64)>,
    }
}

wire_union! {
    /// Something the client tells a plugin, delivered on the session's
    /// thread between ticks, in the order it happened. New kinds come with
    /// new API minor versions (marked `since`), so a plugin matches with a
    /// catch-all arm; the client sends a plugin only the kinds of the API
    /// version it was built for.
    #[non_exhaustive]
    pub enum Event: "event" {
        /// A window attached to (`true`) or left (`false`) the session.
        0 => WindowAttached(bool),
        /// One of the plugin's registered verbs was typed.
        1 => Command(CommandCall),
        /// A chat line arrived (sent only to plugins with the `chat`
        /// permission).
        2 => ChatReceived(ChatLine),
        /// The character finished entering the world (again after every
        /// reconnect).
        3 => LoginComplete,
        /// The character is leaving the world; its state is still
        /// readable.
        4 => Logoff,
        /// The character died: the server's death message.
        5 => LocalPlayerDied(String),
        /// An object was created, updated, appraised, moved or released.
        6 => ObjectChanged(crate::world::ObjectChange),
        /// A portal or login transition moved on.
        7 => PortalTransition(crate::world::PortalTransition),
        /// The server asked a yes/no question.
        8 => ConfirmationRequested(crate::world::Confirmation),
        /// An outside container (a corpse, a chest) opened.
        9 => ContainerOpened(u32),
        /// The outside container that was open closed.
        10 => ContainerClosed(u32),
        /// The selection changed.
        11 => SelectionChanged(crate::world::SelectionChange),
        /// An activation (door, lever, portal, NPC) ended.
        12 => ActivationCompleted(crate::world::ActivationCompletion),
        /// A use the client awaited ended.
        13 => ItemUseCompleted(crate::items::ItemUseCompletion),
        /// An item went on or came off (server-confirmed).
        14 => EquipmentPlacement(crate::items::EquipmentObservation),
        /// The secure trade changed.
        15 => Trade(crate::trade::TradeEvent),
        /// The vendor changed.
        16 => Vendor(crate::trade::VendorEvent),
        /// The walk the client plans changed state, or a new one began.
        17 => NavigationChanged(crate::navigation::GoToReport),
        /// The character's movement snapshot changed (at most once a
        /// pass).
        18 => NavigationSnapshotChanged(crate::navigation::NavigationSnapshot),
        /// A link in the chat transcript was clicked.
        19 => ChatLinkClicked(ChatLinkClicked),
        /// A network request or socket moved on (only to the plugin that
        /// made it).
        20 => Net(crate::net::NetEvent),
        /// An object appeared in the world, with where it stands (OpenAC
        /// `IEvents.EntitySpawned`); a plugin just enabled first hears
        /// one for every object already there.
        21 => EntitySpawned(crate::world::EntitySnapshot),
        /// What the character's experience reads changed (the
        /// description arrived, an update, a level-up, the character left
        /// the world): the new reading, none when there is none.
        22 => ExperienceChanged(Option<crate::character::CharacterExperience>),
        /// The player picked another plugin theme (OpenAC
        /// `IUiRegistry.ThemeChanged`): the new one. Never raised on a
        /// session without a window.
        23 => ThemeChanged(crate::ui::ThemeInfo),
        /// A path preview the plugin asked for ended (only to that
        /// plugin).
        24 => NavigationPlan(crate::navigation::NavigationPlan),
        /// A notice went to the status line (OpenAC
        /// `IEvents.StatusTextReceived`): its text, such as the server's
        /// "The Corpse of ... is already in use by ...!". Raised for every
        /// status notice (the server's transient text, a failure worded as
        /// client-local text, a plugin's post in that class), in order with
        /// the chat lines, whether or not a chat filter hides it from the
        /// screen. Never in the transcript, so never a
        /// [`Self::ChatReceived`] (sent only to plugins with the `chat`
        /// permission, built for API 1.1 or later).
        25 => StatusTextReceived(String) since (1, 1),
        /// A reach search the plugin asked for ended (only to that plugin;
        /// API 1.5).
        26 => NavigationReach(crate::navigation::NavigationReach) since (1, 5),
        /// One of the plugin's panels or canvases was used ([`crate::ui`];
        /// the tag is `crate::ui::EVENT_TAG`).
        100 => Ui(crate::ui::UiEvent),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn events_round_trip_and_unknown_tags_are_refused() {
        let events = [
            Event::WindowAttached(true),
            Event::Command(CommandCall {
                verb: "hello".into(),
                arguments: "a b".into(),
                raw: "/hello a b".into(),
            }),
            Event::ChatReceived(ChatLine {
                sequence: 9,
                sender_id: 0x5000_0001,
                kind: 0,
                sender: "Horan".into(),
                text: "Horan says, \"hi\"".into(),
                channel: String::new(),
                log_text_type: 2,
                combat_kind: 0,
                channel_id: 0,
                display_text: String::new(),
                received_unix_ms: 1_700_000_000_000,
            }),
            Event::LoginComplete,
            Event::LocalPlayerDied("You died.".into()),
            Event::ContainerOpened(7),
            Event::StatusTextReceived(
                "The Corpse of War Armoredillo is already in use by Unkle Leo!".into(),
            ),
        ];
        for event in events {
            assert_eq!(Event::from_bytes(&event.to_bytes()), Ok(event));
        }
        assert_eq!(
            Event::StatusTextReceived("x".into()).to_bytes()[..4],
            25u32.to_le_bytes()
        );
        assert_eq!(
            Event::from_bytes(&[0xEE, 0, 0, 0]),
            Err(DecodeError::UnknownTag {
                what: "event",
                tag: 0xEE
            })
        );
        assert_eq!(
            LogLevel::from_bytes(&[4]),
            Err(DecodeError::UnknownTag {
                what: "log level",
                tag: 4
            })
        );
    }

    #[test]
    fn the_status_text_event_came_with_api_1_1() {
        let v = crate::ApiVersion::new;
        assert_eq!(Event::StatusTextReceived(String::new()).since(), v(1, 1));
        assert_eq!(Event::LoginComplete.since(), v(1, 0));
        assert_eq!(Event::ContainerOpened(1).since(), v(1, 0));
        // API 1.0's events leave it out; 1.1 has every kind, and 1.2 adds
        // a field to the world object an object change carries.
        assert_ne!(Event::shape_at(v(1, 0)), Event::SHAPE);
        assert_ne!(Event::shape_at(v(1, 1)), Event::shape_at(v(1, 0)));
        assert_ne!(Event::shape_at(v(1, 1)), Event::SHAPE);
        assert_eq!(Event::shape_at(crate::API_CURRENT), Event::SHAPE);
    }
}
