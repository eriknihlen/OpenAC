//! Every client slash command, on a session with a window and one without
//! (OpenAC `Scenarios/ClientCommandParityTests.cs`): none fails, every one
//! that does not need a window sends and says the same on both, and the
//! ones that do need a window ask the attached window, or say they need one.
//!
//! OpenAC builds a fresh pair of arms per command; here one pair types every
//! command in turn, the same lines in the same order on both, so whatever a
//! command leaves behind is left behind on both. Each command is typed as a
//! player types it, with OpenAC's arguments.
//!
//! The commands that need a window are the ones this client hands to the
//! window's interface (`WindowRequest`): the frame rate, the render options
//! and the four interface layouts. OpenAC also lists `/die` and `/house
//! abandon`, whose confirmation is a window's dialog there; here the
//! question is the session's own (`ClientConfirmation`), asked the same way
//! with or without a window, so the two are compared with the rest.

use ac_runtime::communication::{
    ChatEntryCommand, ClientCommandId as C, NEEDS_A_WINDOW, is_client_command,
};
use ac_runtime::session::PresentationEvent;

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::scenarios::chat_entry::{entry, interface_text};
use crate::host_parity::world;

/// The line a player types for `id`, with the arguments OpenAC runs it with
/// (`ClientCommandParityTests.ArgumentsFor`). The match has no catch-all, so
/// a new command cannot be left out of this walk.
fn line_for(id: C) -> &'static str {
    match id {
        C::LifestoneRecall => "/lifestone",
        C::MarketplaceRecall => "/marketplace",
        C::PkArenaRecall => "/pkarena",
        C::PkLiteArenaRecall => "/pklarena",
        C::EnterPkLite => "/pklite",
        C::HouseRecall => "/house recall",
        C::MansionRecall => "/house mansion_recall",
        C::QueryAge => "/age",
        C::QueryBirth => "/birth",
        C::ToggleFrameRate => "/framerate",
        C::TogglePersistentDaylight => "/day",
        C::RenderOption => "/render radius 12",
        C::ToggleUiLock => "/lockui",
        C::ShowVersion => "/version",
        C::ShowLocation => "/loc",
        C::ShowLastCorpseLocation => "/corpse",
        C::Die => "/die",
        C::ClearChat => "/clear",
        C::ChatLogFile => "/log",
        C::SaveUi => "/saveui layout",
        C::LoadUi => "/loadui layout",
        C::SaveAutoUi => "/saveautoui",
        C::LoadAutoUi => "/loadautoui",
        C::Away => "/afk msg back soon",
        C::Consent => "/consent who",
        C::Emote => "/emote wave",
        C::ListEmotes => "/emotes",
        C::Friends => "/friends online",
        C::FriendsAdd => "/friends_add Bob",
        C::FriendsRemove => "/friends_remove Bob",
        C::Squelch => "/squelch Bob",
        C::Unsquelch => "/unsquelch Bob",
        C::Filter => "/filter -Speech",
        C::Unfilter => "/unfilter -Speech",
        C::ListMessageTypes => "/messagetypes",
        C::FillComponents => "/fillcomps taper 500",
        C::Endurance => "/endurance",
        C::Speaker => "/speaker",
        C::SetChatTitle => "/title Adventurer",
        C::ChatToggle => "/chat off",
        C::NoTellToggle => "/notell on",
        C::JoinChannel => "/join general",
        C::LeaveChannel => "/leave general",
        C::Permit => "/permit add Bob",
        C::HouseAvailableList => "/hslist cottage",
        C::IndexChannels => "/index",
        C::ListChannel => "/clist general",
        C::OnChannel => "/on general",
        C::OffChannel => "/off general",
        C::AllegianceHometown => "/allegiance hometown",
        C::AllegianceInfo => "/allegiance info",
        C::AllegianceBoot => "/allegiance boot Bob",
        C::AllegianceBan => "/allegiance ban list",
        C::AllegianceChat => "/allegiance chat on",
        C::AllegianceBroadcast => "/allegiance broadcast hello",
        C::AllegianceOfficer => "/allegiance officer list",
        C::AllegianceOfficerTitle => "/allegiance title list",
        C::AllegianceName => "/allegiance name",
        C::AllegianceLock => "/allegiance lock on",
        C::AllegianceHouse => "/allegiance house",
        C::AllegianceMotd => "/allegiance motd",
        C::HouseAbandon => "/house abandon",
        C::HouseOpenStatus => "/house open",
        C::HouseStorage => "/house storage list",
        C::HouseBoot => "/house boot Bob",
        C::HouseBootAll => "/house boot_all",
        C::HouseGuests => "/house guest",
        C::HouseHooks => "/house hooks on",
        C::AllegianceUnrecognizedSubcommand => "/allegiance nonsense",
        C::HouseUnrecognizedSubcommand => "/house nonsense",
    }
}

/// Every command, in the catalog's order.
const ALL: &[C] = &[
    C::LifestoneRecall,
    C::MarketplaceRecall,
    C::PkArenaRecall,
    C::PkLiteArenaRecall,
    C::EnterPkLite,
    C::HouseRecall,
    C::MansionRecall,
    C::QueryAge,
    C::QueryBirth,
    C::ToggleFrameRate,
    C::TogglePersistentDaylight,
    C::RenderOption,
    C::ToggleUiLock,
    C::ShowVersion,
    C::ShowLocation,
    C::ShowLastCorpseLocation,
    C::Die,
    C::ClearChat,
    C::ChatLogFile,
    C::SaveUi,
    C::LoadUi,
    C::SaveAutoUi,
    C::LoadAutoUi,
    C::Away,
    C::Consent,
    C::Emote,
    C::ListEmotes,
    C::Friends,
    C::FriendsAdd,
    C::FriendsRemove,
    C::Squelch,
    C::Unsquelch,
    C::Filter,
    C::Unfilter,
    C::ListMessageTypes,
    C::FillComponents,
    C::Endurance,
    C::Speaker,
    C::SetChatTitle,
    C::ChatToggle,
    C::NoTellToggle,
    C::JoinChannel,
    C::LeaveChannel,
    C::Permit,
    C::HouseAvailableList,
    C::IndexChannels,
    C::ListChannel,
    C::OnChannel,
    C::OffChannel,
    C::AllegianceHometown,
    C::AllegianceInfo,
    C::AllegianceBoot,
    C::AllegianceBan,
    C::AllegianceChat,
    C::AllegianceBroadcast,
    C::AllegianceOfficer,
    C::AllegianceOfficerTitle,
    C::AllegianceName,
    C::AllegianceLock,
    C::AllegianceHouse,
    C::AllegianceMotd,
    C::HouseAbandon,
    C::HouseOpenStatus,
    C::HouseStorage,
    C::HouseBoot,
    C::HouseBootAll,
    C::HouseGuests,
    C::HouseHooks,
    C::AllegianceUnrecognizedSubcommand,
    C::HouseUnrecognizedSubcommand,
];

/// The commands this client hands to an attached window's interface.
const NEED_A_WINDOW: &[C] = &[
    C::ToggleFrameRate,
    C::RenderOption,
    C::SaveUi,
    C::LoadUi,
    C::SaveAutoUi,
    C::LoadAutoUi,
];

/// What the arm put to the player since `chat`/`told`: the feed's lines,
/// then the client's own text, without the sync markers.
fn said(arm: &Arm, chat: usize, told: usize) -> Vec<String> {
    arm.chat[chat..]
        .iter()
        .map(|c| c.text.clone())
        .chain(interface_text(arm)[told..].iter().cloned())
        .filter(|t| !t.contains(world::SYNC_MARKER))
        .collect()
}

fn window_requests(arm: &Arm, from: usize) -> usize {
    arm.presented[from.min(arm.presented.len())..]
        .iter()
        .filter(|e| matches!(e, PresentationEvent::WindowRequest(_)))
        .count()
}

// OpenAC `ClientCommandParityTests.NoClientCommandThrowsOnEitherClient`,
// `EveryClientCommandThatDoesNotNeedAWindowBehavesIdentically` and
// `ACommandThatNeedsAWindowSaysSoRatherThanDoingNothing`, in one walk: a
// command that failed would leave its session unable to answer the next one.
#[test]
fn every_client_command_answers_and_those_without_a_window_need_agree() {
    run(|arm, transcript| {
        for id in ALL {
            let line = line_for(*id);
            assert!(is_client_command(line), "{line} is not a client command");
            transcript.step(&format!("{id:?}"));
            let (chat, told, shown) = (
                arm.chat.len(),
                interface_text(arm).len(),
                arm.presented.len(),
            );
            entry(arm, ChatEntryCommand::Submit(Some(line.to_owned())));
            arm.sync();
            let sent = arm.outbound();
            let said = said(arm, chat, told);
            if NEED_A_WINDOW.contains(id) {
                // The windowed arm asks its window and the windowless one
                // says it has none; what either sent is the same.
                transcript.record("sent", &sent);
                if arm.windowed {
                    assert_eq!(
                        window_requests(arm, shown),
                        1,
                        "{line} asked the window nothing"
                    );
                    assert!(
                        !said.iter().any(|l| l.contains(NEEDS_A_WINDOW)),
                        "{line}: {said:?}"
                    );
                } else {
                    assert!(
                        said.iter().any(|l| l.contains(NEEDS_A_WINDOW)),
                        "{line} said {said:?} without a window instead of saying it needs one"
                    );
                }
            } else {
                transcript.record("sent", &sent);
                transcript.record("said", &said);
                assert_eq!(
                    window_requests(arm, shown),
                    0,
                    "{}: {line} asked a window",
                    arm.name()
                );
                // Two sessions that both ignored every command would agree,
                // so a few are held to what they really do.
                match id {
                    C::LifestoneRecall | C::QueryAge => {
                        assert_eq!(sent.len(), 1, "{}: {line} sent {sent:?}", arm.name());
                    }
                    C::ShowLocation | C::ShowVersion | C::Speaker => {
                        assert!(!said.is_empty(), "{}: {line} said nothing", arm.name());
                    }
                    _ => {}
                }
            }
        }
    });
}
