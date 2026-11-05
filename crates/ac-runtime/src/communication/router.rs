//! Where a submitted chat line goes: one of the client's own commands, a
//! line of chat on a channel, a raw numbered-channel line, or server text.
//!
//! The router decides; it sends nothing itself. It answers with at most one
//! [`RoutedCommand`] and says anything the player should read through
//! [`ChatFeedback`].

use ac_core::chat::{LogTextType, weenie_errors};

use super::catalog::{self, ClientCommandId};
use super::help;
use super::input::{self, ChatChannelKind};

/// What happened to a submitted line.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SubmitOutcome {
    /// Nothing was typed.
    Empty,
    /// The client answered it (a client command or a refusal).
    ClientHandled,
    /// A `/` or `@` with no verb.
    UnknownCommand,
    /// It goes to the server.
    Sent,
    /// It went nowhere (a room verb with no text).
    Dropped,
}

/// A chat line on its way out.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SendChat {
    /// Where it goes.
    pub channel: ChatChannelKind,
    /// A tell's addressee by name.
    pub target_name: Option<String>,
    /// The text.
    pub text: String,
    /// An addressee picked in the world, 0 when none; the line is then aimed
    /// at that object instead of looking a name up.
    pub target_guid: u32,
}

/// What the router hands on.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RoutedCommand {
    /// Run a client command.
    Client {
        /// The command.
        command: ClientCommandId,
        /// Its arguments.
        arguments: String,
    },
    /// Say this text to the server as typed (`@` commands).
    ServerCommand(String),
    /// A chat line.
    Chat(SendChat),
    /// A line on a numbered channel named by a tag only (`/admin hi`).
    RawChannel {
        /// The channel number.
        channel_id: u32,
        /// The text.
        text: String,
    },
}

/// What the router may tell the player and the reply targets it reads.
pub trait ChatFeedback {
    /// Client-local text: shown in the status overlay, not the transcript.
    fn show_interface_text(&mut self, text: &str);
    /// A line in the transcript.
    fn show_system_message(&mut self, text: &str);
    /// Who last sent the player a tell.
    fn last_incoming_tell_sender(&self) -> Option<&str>;
    /// Who the player last sent a tell to.
    fn last_outgoing_tell_target(&self) -> Option<&str>;
}

/// Where the default line goes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Defaults<'a> {
    /// The channel a line without a verb goes to.
    pub channel: ChatChannelKind,
    /// The addressee of a plain line on the tell channel.
    pub tell_target: Option<&'a str>,
    /// That addressee's object id, when picked in the world.
    pub tell_target_guid: u32,
}

impl Default for Defaults<'_> {
    fn default() -> Self {
        Self {
            channel: ChatChannelKind::Say,
            tell_target: None,
            tell_target_guid: 0,
        }
    }
}

/// Route one submitted line.
pub fn submit(
    raw: &str,
    feedback: &mut impl ChatFeedback,
    defaults: Defaults<'_>,
) -> (SubmitOutcome, Option<RoutedCommand>) {
    let typed = raw.trim();
    if typed.is_empty() {
        return (SubmitOutcome::Empty, None);
    }
    // A line opening with ':' or ';' is an emote.
    let rewritten;
    let trimmed = if let Some(rest) = typed.strip_prefix([':', ';']) {
        rewritten = format!("@emote {rest}");
        rewritten.as_str()
    } else {
        typed
    };

    if let Some(result) = client_command(trimmed, feedback) {
        return result;
    }
    beyond_client_commands(trimmed, feedback, defaults)
}

fn client_command(
    trimmed: &str,
    feedback: &mut impl ChatFeedback,
) -> Option<(SubmitOutcome, Option<RoutedCommand>)> {
    if let Some(matched) = catalog::try_match(trimmed) {
        if !matched.has_valid_arguments {
            if let Some(refusal) = matched.invalid_arguments_text {
                feedback.show_interface_text(refusal);
            } else {
                match weenie_errors::resolve(0x026, None) {
                    Some((text, LogTextType::ClientLocal)) => feedback.show_interface_text(&text),
                    Some((text, _)) => feedback.show_system_message(&text),
                    None => feedback.show_system_message("That is not a valid command."),
                }
            }
            return Some((SubmitOutcome::ClientHandled, None));
        }
        return Some((
            SubmitOutcome::ClientHandled,
            Some(RoutedCommand::Client {
                command: matched.command,
                arguments: matched.arguments,
            }),
        ));
    }
    if let Some(verb) = help::help_request(trimmed) {
        for line in help::lines(verb) {
            feedback.show_system_message(&line);
        }
        return Some((SubmitOutcome::ClientHandled, None));
    }
    None
}

fn beyond_client_commands(
    trimmed: &str,
    feedback: &mut impl ChatFeedback,
    defaults: Defaults<'_>,
) -> (SubmitOutcome, Option<RoutedCommand>) {
    let mut chars = trimmed.chars();
    let first = chars.next();
    let second = chars.next();
    if matches!(first, Some('/' | '@')) && !second.is_some_and(char::is_alphabetic) {
        feedback.show_system_message(&format!(
            "Unknown command: {}. Type /help for the list of supported commands.",
            input::verb_token(trimmed)
        ));
        return (SubmitOutcome::UnknownCommand, None);
    }

    if let Some(routed) = channel_fallback(trimmed) {
        return (SubmitOutcome::Sent, Some(routed));
    }

    if let Some(command) = server_command(trimmed) {
        return (
            SubmitOutcome::Sent,
            Some(RoutedCommand::ServerCommand(command)),
        );
    }

    if input::is_bare_registered_channel_verb(trimmed) {
        feedback.show_interface_text("You must specify the text you wish to say!");
        return (SubmitOutcome::ClientHandled, None);
    }
    if input::is_reply_missing_last_teller(trimmed, feedback.last_incoming_tell_sender()) {
        feedback.show_interface_text("Someone must @tell you first!");
        return (SubmitOutcome::ClientHandled, None);
    }
    if input::is_retell_missing_last_tellee(trimmed, feedback.last_outgoing_tell_target()) {
        feedback.show_interface_text("You must first provide a name using @tell");
        return (SubmitOutcome::ClientHandled, None);
    }

    match input::parse(
        trimmed,
        defaults.channel,
        feedback.last_incoming_tell_sender(),
        feedback.last_outgoing_tell_target(),
        defaults.tell_target,
        defaults.tell_target_guid,
    ) {
        Some(parsed) => (
            SubmitOutcome::Sent,
            Some(RoutedCommand::Chat(SendChat {
                channel: parsed.channel,
                target_name: parsed.target_name,
                text: parsed.text,
                target_guid: parsed.target_guid,
            })),
        ),
        None => (SubmitOutcome::Dropped, None),
    }
}

/// `/tag text` for a numbered channel that has no chat verb of its own.
fn channel_fallback(trimmed: &str) -> Option<RoutedCommand> {
    if !trimmed.starts_with(['/', '@']) {
        return None;
    }
    let verb = input::verb_token(trimmed);
    let tag = verb[1..].trim_end_matches(',');
    if catalog::is_catalog_verb(tag) || input::is_known_verb(&format!("/{tag}")) {
        return None;
    }
    let channel_id = input::resolve_channel_tag(tag)?;
    let text = trimmed[verb.len()..].trim();
    if text.is_empty() {
        return None;
    }
    Some(RoutedCommand::RawChannel {
        channel_id,
        text: text.to_owned(),
    })
}

/// A `/` or `@` line whose verb is no chat verb goes to the server in its
/// `@` form.
fn server_command(trimmed: &str) -> Option<String> {
    if !trimmed.starts_with(['/', '@']) {
        return None;
    }
    let verb = input::verb_token(trimmed);
    if input::is_known_verb(&format!("/{}", &verb[1..])) {
        return None;
    }
    Some(format!("@{}", &trimmed[1..]))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct Recorder {
        interface: Vec<String>,
        system: Vec<String>,
        last_teller: Option<String>,
        last_tellee: Option<String>,
    }

    impl ChatFeedback for Recorder {
        fn show_interface_text(&mut self, text: &str) {
            self.interface.push(text.to_owned());
        }
        fn show_system_message(&mut self, text: &str) {
            self.system.push(text.to_owned());
        }
        fn last_incoming_tell_sender(&self) -> Option<&str> {
            self.last_teller.as_deref()
        }
        fn last_outgoing_tell_target(&self) -> Option<&str> {
            self.last_tellee.as_deref()
        }
    }

    fn route(line: &str) -> (SubmitOutcome, Option<RoutedCommand>, Recorder) {
        let mut feedback = Recorder::default();
        let (outcome, command) = submit(line, &mut feedback, Defaults::default());
        (outcome, command, feedback)
    }

    fn chat(channel: ChatChannelKind, target: Option<&str>, text: &str) -> RoutedCommand {
        RoutedCommand::Chat(SendChat {
            channel,
            target_name: target.map(str::to_owned),
            text: text.to_owned(),
            target_guid: 0,
        })
    }

    // Golden cases: OpenAC tests/AcDream.UI.Abstractions.Tests/Panels/Chat/ChatCommandRouterTests.cs
    // and tests/AcDream.Runtime.Tests/Chat/ChatCommandRouterFeedbackRoutingTests.cs.

    #[test]
    fn plain_text_goes_to_the_default_channel() {
        let (outcome, command, _) = route("hello there");
        assert_eq!(outcome, SubmitOutcome::Sent);
        assert_eq!(
            command,
            Some(chat(ChatChannelKind::Say, None, "hello there"))
        );
        let mut feedback = Recorder::default();
        let (_, command) = submit(
            "hi",
            &mut feedback,
            Defaults {
                channel: ChatChannelKind::Fellowship,
                ..Defaults::default()
            },
        );
        assert_eq!(command, Some(chat(ChatChannelKind::Fellowship, None, "hi")));
    }

    #[test]
    fn empty_input_does_nothing() {
        let (outcome, command, _) = route("   ");
        assert_eq!((outcome, command), (SubmitOutcome::Empty, None));
    }

    #[test]
    fn client_commands_route_with_their_arguments() {
        for (line, id, arguments) in [
            ("/clear", ClientCommandId::ClearChat, ""),
            ("/ls", ClientCommandId::LifestoneRecall, ""),
            ("@LS", ClientCommandId::LifestoneRecall, ""),
            ("@pklite", ClientCommandId::EnterPkLite, ""),
            ("/endurance", ClientCommandId::Endurance, ""),
            (":waves", ClientCommandId::Emote, "waves"),
            (";waves", ClientCommandId::Emote, "waves"),
            (
                "/allegiance boot Lord Bob",
                ClientCommandId::AllegianceBoot,
                "Lord Bob",
            ),
        ] {
            let (outcome, command, feedback) = route(line);
            assert_eq!(outcome, SubmitOutcome::ClientHandled, "{line}");
            assert_eq!(
                command,
                Some(RoutedCommand::Client {
                    command: id,
                    arguments: arguments.into()
                }),
                "{line}"
            );
            assert!(feedback.interface.is_empty() && feedback.system.is_empty());
        }
    }

    #[test]
    fn bad_arguments_get_the_commands_refusal_as_interface_text() {
        for (line, refusal) in [
            (
                "/ls now",
                "Please see @help lifestone for more information on how to use this command.",
            ),
            (
                "/mar now",
                "Please see @help marketplace for more information on how to use this command.",
            ),
            ("/pka now", "That is not a valid command."),
            (
                "/allegiance nope Bob",
                "Please see @help Allegiance for more information on how to use this command.",
            ),
            ("/clist", "Please specify the channel name."),
            (
                "/hslist nonsense",
                "Please see @help hslist for more information on how to use this command",
            ),
        ] {
            let (outcome, command, feedback) = route(line);
            assert_eq!((outcome, command), (SubmitOutcome::ClientHandled, None));
            assert_eq!(feedback.interface, [refusal], "{line}");
            assert!(feedback.system.is_empty());
        }
    }

    #[test]
    fn unknown_verbs_become_server_commands() {
        let (outcome, command, feedback) = route("/notacommand");
        assert_eq!(outcome, SubmitOutcome::Sent);
        assert_eq!(
            command,
            Some(RoutedCommand::ServerCommand("@notacommand".into()))
        );
        assert!(feedback.system.is_empty());
        for line in ["/ci 629 5", "@ci 629 5"] {
            let mut feedback = Recorder::default();
            let (_, command) = submit(
                line,
                &mut feedback,
                Defaults {
                    channel: ChatChannelKind::Fellowship,
                    ..Defaults::default()
                },
            );
            assert_eq!(
                command,
                Some(RoutedCommand::ServerCommand("@ci 629 5".into()))
            );
        }
    }

    #[test]
    fn unregistered_channel_tags_send_raw_or_pass_through() {
        let (outcome, command, _) = route("/admin server is misbehaving");
        assert_eq!(outcome, SubmitOutcome::Sent);
        assert_eq!(
            command,
            Some(RoutedCommand::RawChannel {
                channel_id: 2,
                text: "server is misbehaving".into()
            })
        );
        let (outcome, command, feedback) = route("/sentinel");
        assert_eq!(outcome, SubmitOutcome::Sent);
        assert_eq!(
            command,
            Some(RoutedCommand::ServerCommand("@sentinel".into()))
        );
        assert!(feedback.interface.is_empty());
    }

    #[test]
    fn a_registered_channel_verb_is_chat() {
        let (outcome, command, _) = route("/f hi gang");
        assert_eq!(outcome, SubmitOutcome::Sent);
        assert_eq!(
            command,
            Some(chat(ChatChannelKind::Fellowship, None, "hi gang"))
        );
    }

    #[test]
    fn bare_numbered_channel_verbs_ask_for_text_and_room_verbs_drop() {
        for line in [
            "/g",
            "/f",
            "/fellowship",
            "/a",
            "/ab",
            "/m",
            "/p",
            "/v",
            "/c",
        ] {
            let (outcome, command, feedback) = route(line);
            assert_eq!((outcome, command), (SubmitOutcome::ClientHandled, None));
            assert_eq!(
                feedback.interface,
                ["You must specify the text you wish to say!"]
            );
        }
        for line in [
            "/lfg",
            "/trade",
            "/general",
            "/roleplay",
            "/society",
            "/olthoi",
        ] {
            let (outcome, command, feedback) = route(line);
            assert_eq!((outcome, command), (SubmitOutcome::Dropped, None), "{line}");
            assert!(feedback.interface.is_empty() && feedback.system.is_empty());
        }
    }

    #[test]
    fn reply_and_retell_refuse_without_a_target() {
        let (outcome, command, feedback) = route("/r hi there");
        assert_eq!((outcome, command), (SubmitOutcome::ClientHandled, None));
        assert_eq!(feedback.interface, ["Someone must @tell you first!"]);
        let (outcome, command, feedback) = route("/rt hello");
        assert_eq!((outcome, command), (SubmitOutcome::ClientHandled, None));
        assert_eq!(
            feedback.interface,
            ["You must first provide a name using @tell"]
        );

        let mut feedback = Recorder {
            last_teller: Some("Bestie".into()),
            ..Recorder::default()
        };
        let (outcome, command) = submit("/r hi there", &mut feedback, Defaults::default());
        assert_eq!(outcome, SubmitOutcome::Sent);
        assert_eq!(
            command,
            Some(chat(ChatChannelKind::Tell, Some("Bestie"), "hi there"))
        );
        assert!(feedback.interface.is_empty());
    }

    #[test]
    fn a_degenerate_prefix_is_an_unknown_command_in_the_transcript() {
        let (outcome, command, feedback) = route("/");
        assert_eq!((outcome, command), (SubmitOutcome::UnknownCommand, None));
        assert!(feedback.interface.is_empty());
        assert_eq!(feedback.system.len(), 1);
        assert!(feedback.system[0].contains("Unknown command:"));
    }

    // Golden text: OpenAC tests/AcDream.UI.Abstractions.Tests/Panels/Chat/
    // RetailCommandHelpTableTests.cs (`*_ThroughRouter_*`) and
    // ChatCommandRouterTests.cs (`HelpVerb_*`).

    #[test]
    fn help_answers_locally_in_the_transcript() {
        for (line, detail) in [
            ("/help death", help::DEATH_GROUP_DETAIL),
            ("/help die", help::DIE_DETAIL),
            ("/help lifestone", help::LIFESTONE_DETAIL),
            ("/help messagetypes", help::MESSAGE_TYPES_DETAIL),
            ("/help mr", help::MONARCH_REPLY),
            ("/help pr", help::PATRON_REPLY),
            ("@? /house", help::HOUSE_OVERVIEW),
        ] {
            let (outcome, command, feedback) = route(line);
            assert_eq!((outcome, command), (SubmitOutcome::ClientHandled, None));
            assert!(feedback.interface.is_empty());
            assert_eq!(
                feedback.system,
                [
                    help::HELP_PREFIX_NOTE.to_owned(),
                    format!("{}{detail}", help::FOR_MORE_INFORMATION_PREFIX)
                ],
                "{line}"
            );
        }
        let (_, _, feedback) = route("/help lifestone");
        assert!(feedback.system[1].contains("Returns you to the last lifestone"));
    }

    #[test]
    fn bare_help_lists_the_topics() {
        for line in ["/help", "/?", "@HELP", "@?"] {
            let (outcome, command, feedback) = route(line);
            assert_eq!((outcome, command), (SubmitOutcome::ClientHandled, None));
            assert_eq!(
                feedback.system,
                [help::HELP_PREFIX_NOTE, help::AVAILABLE_HELP_LISTING]
            );
        }
    }

    #[test]
    fn help_for_an_unknown_or_helpless_verb_is_unknown_command() {
        for line in [
            "/help nonsenseverb",
            "/help index",
            "/help clist",
            "/help on",
            "/help off",
        ] {
            let (outcome, command, feedback) = route(line);
            assert_eq!((outcome, command), (SubmitOutcome::ClientHandled, None));
            assert!(feedback.interface.is_empty());
            assert_eq!(feedback.system, [help::UNKNOWN_COMMAND], "{line}");
        }
    }

    #[test]
    fn a_tell_to_a_picked_target_carries_its_id() {
        // OpenAC tests/AcDream.Runtime.Tests/Chat/TellTargetingTests.cs
        let mut feedback = Recorder::default();
        let (outcome, command) = submit(
            "I would like to trade",
            &mut feedback,
            Defaults {
                channel: ChatChannelKind::Tell,
                tell_target: Some("Aun Tanua"),
                tell_target_guid: 0x8000_ABCD,
            },
        );
        assert_eq!(outcome, SubmitOutcome::Sent);
        assert_eq!(
            command,
            Some(RoutedCommand::Chat(SendChat {
                channel: ChatChannelKind::Tell,
                target_name: Some("Aun Tanua".into()),
                text: "I would like to trade".into(),
                target_guid: 0x8000_ABCD,
            }))
        );
        // A typed tell ignores the picked target.
        let (_, command) = submit(
            "/tell Bob, hello",
            &mut feedback,
            Defaults {
                channel: ChatChannelKind::Tell,
                tell_target: Some("Aun Tanua"),
                tell_target_guid: 0x8000_ABCD,
            },
        );
        assert_eq!(
            command,
            Some(chat(ChatChannelKind::Tell, Some("Bob"), "hello"))
        );
    }
}
