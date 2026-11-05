//! Chat and the social state of one session: the transcript, reply targets,
//! chat rooms, friends, squelches, the quest tracker, the journal and the
//! `@log` file.
//!
//! One [`Communication`] per session, owned by the session thread. Server
//! messages come in through [`Communication::apply`]; typed lines and host
//! requests through [`Communication::submit`] and the `send_*` methods,
//! which queue their requests on the session's [`Outbox`]. Every line the
//! player should read comes out of [`Communication::drain_events`] as owned
//! data.
//!
//! Server game events are routed with a `match` on the event type rather
//! than `ac_proto::GameEventDispatcher`: there is one owner and one handler
//! per event here, so a registry would add indirection and nothing else.

mod administration;
mod catalog;
mod chat_entry;
mod contracts;
mod dispatcher;
mod feed;
pub mod help;
mod input;
mod journal;
mod log_file;
mod login_commands;
mod outbound;
mod poses;
mod router;
mod turbine;

#[cfg(test)]
mod tests;

use std::collections::BTreeMap;
use std::path::PathBuf;

use ac_core::chat::{
    ChatCommandTargetState, ChatEntry, ChatLog, LogTextType, SpewBoxState, TurbineChatRooms,
    TurbineChatState, weenie_errors,
};
use ac_core::quests::ContractCatalog;
use ac_core::social::{
    FriendEntry, FriendsState, FriendsUpdate, FriendsUpdateType, SquelchDatabase, SquelchInfo,
    SquelchState,
};
use ac_proto::chat::{self as wire_chat, ChannelBroadcast, QueryAgeResponse, Tell};
use ac_proto::turbine_chat::{Payload, TurbineChat, TurbineChatChannels};
use ac_proto::{GameEventEnvelope, GameEventType, Inbound, ProtoError};

use crate::local_time::LocalZone;

pub use ac_core::chat::{ChatKind, CombatLineKind};
pub use ac_core::journal::JournalPage;
pub use administration::AdministrationRequest;
pub use catalog::{CatalogMatch, ClientCommandId};
pub use chat_entry::{ChatEntryCommand, ChatEntryState, HISTORY_LIMIT};
pub use contracts::{ContractState, ContractSummary, TrackedContract};
pub use dispatcher::{
    ClientConfirmation, ClientRequest, ItemUseConfirmation, LocalRequest, MAX_FRIENDS,
    NEEDS_A_WINDOW, WindowRequest,
};
pub use feed::{CLIENT_LOCAL_TAG, ChatLine, format, format_tagged, tag_for};
pub use input::{ChatChannelKind, ParsedInput, expand_reply_abbreviation};
pub use journal::{JournalFile, JournalState, JournalView};
pub use log_file::{ChatLogResult, ChatSessionLog};
pub use login_commands::{LoginCommandFailure, LoginCommandSequence};
pub use outbound::Outbound;
pub use poses::{ChatPose, ChatPoseCatalog, extract_poses};
pub use router::{Defaults, RoutedCommand, SendChat, SubmitOutcome};
pub use turbine::{GateResult, GateStatus, Membership};

/// Whether a typed line is one of the client's own commands (its catalog),
/// which the chat route answers before any verb the runtime registers.
#[must_use]
pub fn is_client_command(line: &str) -> bool {
    catalog::try_match(line.trim()).is_some()
}

/// Something the player should read.
#[derive(Debug, Clone, PartialEq)]
pub enum CommunicationEvent {
    /// A line appended to the transcript.
    Line(ChatLine),
    /// Client-local status text (the overlay at the top of the view, never
    /// the transcript).
    InterfaceText(String),
    /// A notice took the status path, in order with the transcript's
    /// lines: shown ([`Self::InterfaceText`] follows) or dropped by a
    /// plugin's chat filter. Plugins hear it either way.
    StatusNotice {
        /// The client's counter for status notices, from 1, never reset
        /// (a line's sequence plays the same part for chat).
        sequence: u64,
        /// The text, trimmed.
        text: String,
    },
    /// The client asks the player a question (a yes/no dialog); the answer
    /// goes to [`Communication::answer_confirmation`].
    Confirmation(ClientConfirmation),
}

/// Text held for the session's plugin chat filters (see
/// [`Communication::set_plugin_filtering`]), in arrival order.
#[derive(Debug, Clone, PartialEq)]
pub enum HeldText {
    /// A transcript line, not appended yet (no sequence).
    Line(ChatEntry),
    /// Client-local status text for the overlay.
    Status {
        /// The text, trimmed.
        text: String,
        /// When it arrived, UTC milliseconds.
        received_unix_ms: i64,
    },
}

/// A change to the journal.
#[derive(Debug, Clone, PartialEq)]
pub enum JournalCommand {
    /// Add an empty page and read it.
    NewPage,
    /// Delete a page (1-based).
    DeletePage(usize),
    /// Read a page (1-based).
    GotoPage(usize),
    /// Replace the current page's texts.
    Edit {
        /// The short label.
        label: String,
        /// The title.
        title: String,
        /// The notes.
        notes: String,
    },
    /// Record a map location on the current page.
    RecordLocation {
        /// East-west map coordinate.
        x: f32,
        /// North-south map coordinate.
        y: f32,
    },
    /// Set the current page's countdown length.
    SetTimer {
        /// Days.
        days: i32,
        /// Hours.
        hours: i32,
        /// Minutes.
        minutes: i32,
    },
    /// Start the current page's countdown.
    StartTimer,
    /// Stop the countdown.
    ResetTimer,
}

/// Where the communication owner keeps its files.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CommunicationPaths {
    /// Where `@log` names without a directory land; `None` turns `@log`
    /// off (it answers that the file could not be opened).
    pub chat_log_directory: Option<PathBuf>,
    /// Where journals are kept; `None` keeps the journal in memory only.
    pub journal_directory: Option<PathBuf>,
}

/// The facts about the player some commands read, from the world.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PlayerFacts {
    /// The player's object id (0 before entering the world).
    pub guid: u32,
    /// The player's public description bits, when described.
    pub public_weenie_bitfield: Option<u32>,
    /// The player is away from the keyboard.
    pub is_away: bool,
    /// The chat-room membership the character options give.
    pub membership: Membership,
    /// The cell of the player's last corpse outside (their saved position
    /// of that kind), when the server told.
    pub last_outside_corpse_cell: Option<u32>,
    /// A vendor's shop is open.
    pub has_open_vendor: bool,
    /// Where the character's body stands (cell, origin, orientation
    /// `w x y z`), when it has one.
    pub current_position: Option<(u32, [f32; 3], [f32; 4])>,
    /// The character is male (a pose's line reads "his" rather than "her").
    pub male: bool,
    /// A window is attached (window commands need one).
    pub has_window: bool,
}

/// The session's chat and social state.
#[derive(Debug)]
pub struct Communication {
    chat: ChatLog,
    targets: ChatCommandTargetState,
    turbine: TurbineChatState,
    friends: FriendsState,
    squelch: SquelchState,
    spew_box: SpewBoxState,
    contracts: ContractState,
    contract_catalog: std::sync::Arc<ContractCatalog>,
    journal: JournalState,
    journal_file: Option<JournalFile>,
    log_file: Option<ChatSessionLog>,
    display_timestamps: bool,
    time_zone: LocalZone,
    client_version: String,
    events: Vec<CommunicationEvent>,
    parse_failures: BTreeMap<u32, u64>,
    player_guid: u32,
    /// The client's own questions, oldest first: the first is the open one,
    /// the rest wait for it to be answered.
    pending_confirmations: std::collections::VecDeque<ClientConfirmation>,
    /// The server's open question (one at a time).
    server_confirmation: Option<ClientConfirmation>,
    /// The server asked its question after the client asked its own (the
    /// newer dialog is the one on top).
    server_asked_last: bool,
    local_requests: Vec<LocalRequest>,
    /// The one chat entry every front end types into.
    chat_entry: ChatEntryState,
    /// The poses speech can carry (empty until the dats are read).
    chat_poses: std::sync::Arc<ChatPoseCatalog>,
    /// Motions poses in speech asked for, for the movement owner.
    pose_motions: Vec<u32>,
    window_requests: Vec<WindowRequest>,
    /// The session's plugins filter chat: text waits in `held`.
    plugin_filtering: bool,
    held: Vec<HeldText>,
    /// How many status notices were raised: the last one's sequence.
    status_notices: u64,
}

/// The version `/version` reports.
const CLIENT_VERSION: &str = env!("CARGO_PKG_VERSION");

impl Communication {
    /// A fresh owner keeping its files where `paths` says.
    #[must_use]
    pub fn new(paths: CommunicationPaths) -> Self {
        Self {
            chat: ChatLog::new(),
            targets: ChatCommandTargetState::new(),
            turbine: TurbineChatState::new(),
            friends: FriendsState::new(),
            squelch: SquelchState::new(),
            spew_box: SpewBoxState::new(),
            contracts: ContractState::new(),
            contract_catalog: std::sync::Arc::default(),
            journal: JournalState::new(),
            journal_file: paths.journal_directory.map(JournalFile::new),
            log_file: paths.chat_log_directory.map(ChatSessionLog::new),
            display_timestamps: false,
            time_zone: LocalZone::system(),
            client_version: CLIENT_VERSION.to_owned(),
            events: Vec::new(),
            parse_failures: BTreeMap::new(),
            player_guid: 0,
            pending_confirmations: std::collections::VecDeque::new(),
            server_confirmation: None,
            server_asked_last: false,
            local_requests: Vec::new(),
            chat_entry: ChatEntryState::default(),
            chat_poses: std::sync::Arc::default(),
            pose_motions: Vec::new(),
            window_requests: Vec::new(),
            plugin_filtering: false,
            held: Vec::new(),
            status_notices: 0,
        }
    }

    /// Read speech poses from this catalog (the dat's chat pose table).
    pub fn install_chat_poses(&mut self, poses: std::sync::Arc<ChatPoseCatalog>) {
        self.chat_poses = poses;
    }

    /// The poses speech can carry.
    #[must_use]
    pub fn chat_poses(&self) -> &ChatPoseCatalog {
        &self.chat_poses
    }

    /// Take the motions poses in speech asked for, oldest first.
    pub fn take_pose_motions(&mut self) -> Vec<u32> {
        std::mem::take(&mut self.pose_motions)
    }

    /// The question the client is waiting for the player to answer (the
    /// oldest of its questions; the others wait behind it).
    #[must_use]
    pub fn pending_confirmation(&self) -> Option<ClientConfirmation> {
        self.pending_confirmations.front().cloned()
    }

    /// Queue the client's question `confirmation`: it opens (and the host
    /// hears it) when no other of its questions is open, else it waits its
    /// turn.
    pub(crate) fn queue_confirmation(&mut self, confirmation: ClientConfirmation) {
        self.pending_confirmations.push_back(confirmation);
        if self.pending_confirmations.len() == 1 {
            self.open_next_confirmation();
        }
    }

    /// The question now first in line opens: the host hears it.
    fn open_next_confirmation(&mut self) {
        if let Some(next) = self.pending_confirmations.front().cloned() {
            self.server_asked_last = false;
            self.events.push(CommunicationEvent::Confirmation(next));
        }
    }

    /// The question an answer with no question named goes to: the newer of
    /// the client's and the server's open questions (the dialog on top).
    #[must_use]
    pub fn open_confirmation(&self) -> Option<ClientConfirmation> {
        match (
            self.pending_confirmations.front(),
            &self.server_confirmation,
        ) {
            (Some(_), Some(server)) if self.server_asked_last => Some(server.clone()),
            (Some(client), _) => Some(client.clone()),
            (None, server) => server.clone(),
        }
    }

    /// The server's open question.
    #[must_use]
    pub fn server_confirmation(&self) -> Option<&ClientConfirmation> {
        self.server_confirmation.as_ref()
    }

    /// The server asks a question: it is open (and the host hears it)
    /// unless another of its questions is; then it is refused (false), as
    /// the game shows one at a time.
    pub fn server_confirmation_request(
        &mut self,
        request: &ac_proto::social::ConfirmationRequest,
    ) -> bool {
        if self.server_confirmation.is_some() {
            return false;
        }
        let question = ClientConfirmation::Server {
            confirmation_type: request.confirmation_type,
            context_id: request.context_id,
            message: request.message.clone(),
        };
        self.server_confirmation = Some(question.clone());
        self.server_asked_last = true;
        self.events.push(CommunicationEvent::Confirmation(question));
        true
    }

    /// The server withdraws a question; true when it was the open one.
    pub fn server_confirmation_done(&mut self, done: &ac_proto::social::ConfirmationDone) -> bool {
        let open = matches!(
            &self.server_confirmation,
            Some(ClientConfirmation::Server { confirmation_type, context_id, .. })
                if *confirmation_type == done.confirmation_type && *context_id == done.context_id
        );
        if open {
            self.server_confirmation = None;
        }
        open
    }

    /// Ask the player `confirmation` (another owner's question: an item
    /// use); one question is open at a time, a later one waits its turn.
    pub fn ask(&mut self, confirmation: ClientConfirmation) {
        self.queue_confirmation(confirmation);
    }

    /// Take the work client commands handed to other owners, oldest first.
    pub fn take_local_requests(&mut self) -> Vec<LocalRequest> {
        std::mem::take(&mut self.local_requests)
    }

    /// Ask the attached window for something (the session's own requests,
    /// beside the client commands').
    pub fn request_window(&mut self, request: WindowRequest) {
        self.window_requests.push(request);
    }

    /// Take what client commands asked of the attached window, oldest
    /// first.
    pub fn take_window_requests(&mut self) -> Vec<WindowRequest> {
        std::mem::take(&mut self.window_requests)
    }

    /// The status box: the messages on show, newest first (a window that
    /// attaches shows them too).
    #[must_use]
    pub fn status_lines(&self) -> Vec<String> {
        self.spew_box
            .visible()
            .iter()
            .map(|e| e.text.clone())
            .collect()
    }

    /// The transcript.
    #[must_use]
    pub fn chat(&self) -> &ChatLog {
        &self.chat
    }

    /// Every line the transcript holds, oldest first, worded as the chat
    /// box shows them now (a window attaching rebuilds its chat from these).
    #[must_use]
    pub fn transcript_lines(&self) -> Vec<ChatLine> {
        self.chat
            .entries()
            .map(|entry| {
                let utc_offset = self.time_zone.utc_offset_seconds(entry.received_unix_ms);
                ChatLine::from_entry(entry, self.display_timestamps, utc_offset)
            })
            .collect()
    }

    /// The reply and retell targets.
    #[must_use]
    pub fn targets(&self) -> &ChatCommandTargetState {
        &self.targets
    }

    /// The chat-room state.
    #[must_use]
    pub fn turbine(&self) -> &TurbineChatState {
        &self.turbine
    }

    /// The friends list.
    #[must_use]
    pub fn friends(&self) -> &FriendsState {
        &self.friends
    }

    /// The squelch database.
    #[must_use]
    pub fn squelch(&self) -> &SquelchState {
        &self.squelch
    }

    /// The status overlay's queue.
    #[must_use]
    pub fn spew_box(&self) -> &SpewBoxState {
        &self.spew_box
    }

    /// The quest tracker.
    #[must_use]
    pub fn contracts(&self) -> &ContractState {
        &self.contracts
    }

    /// The chat entry every front end types into.
    #[must_use]
    pub fn chat_entry(&self) -> &ChatEntryState {
        &self.chat_entry
    }

    /// The chat entry, to change.
    pub fn chat_entry_mut(&mut self) -> &mut ChatEntryState {
        &mut self.chat_entry
    }

    /// The contract catalog contracts are named from.
    #[must_use]
    pub fn contract_catalog(&self) -> &std::sync::Arc<ContractCatalog> {
        &self.contract_catalog
    }

    /// Name contracts from this catalog (the dat's contract table).
    pub fn install_contract_catalog(&mut self, catalog: std::sync::Arc<ContractCatalog>) {
        self.contract_catalog = catalog;
    }

    /// The quest tracker as a host lists it.
    #[must_use]
    pub fn contract_summaries(&self, now_unix_ms: i64) -> Vec<ContractSummary> {
        self.contracts
            .summaries(&self.contract_catalog, now_unix_ms)
    }

    /// The journal.
    #[must_use]
    pub fn journal(&self) -> &JournalState {
        &self.journal
    }

    /// Game events that failed to parse, by `0xF7B0_0000 | event type`.
    #[must_use]
    pub fn parse_failures(&self) -> &BTreeMap<u32, u64> {
        &self.parse_failures
    }

    /// Show a timestamp before each line (the player's option).
    pub fn set_display_timestamps(&mut self, on: bool) {
        self.display_timestamps = on;
    }

    /// Hide offensive words in what others say (the player's option).
    pub fn set_filter_language(&mut self, on: bool) {
        self.chat.set_filter_language(on);
    }

    /// The patterns the language filter censors against: `None` (no taboo
    /// table) censors every word.
    pub fn set_filter_language_patterns(&mut self, patterns: Option<Vec<String>>) {
        self.chat.set_filter_language_patterns(patterns);
    }

    /// The time zone timestamps are shown in (the system's by default).
    pub fn set_time_zone(&mut self, zone: LocalZone) {
        self.time_zone = zone;
    }

    /// Take what the player should read, oldest first.
    pub fn drain_events(&mut self) -> std::vec::Drain<'_, CommunicationEvent> {
        self.events.drain(..)
    }

    /// Advance the status overlay's clock (seconds on any steady clock).
    pub fn tick(&mut self, now_seconds: f64) {
        self.spew_box.tick(now_seconds);
    }

    /// The character entered the world: their own speech now reads "You",
    /// and their journal is loaded.
    pub fn entered_world(
        &mut self,
        player_guid: u32,
        server_name: &str,
        character_name: &str,
        now_unix_ms: i64,
    ) {
        self.player_guid = player_guid;
        self.chat.set_local_player_guid(player_guid);
        if let Some(file) = &mut self.journal_file
            && let Some(message) = file.load(&mut self.journal, server_name, character_name)
        {
            self.report(now_unix_ms, &message);
        }
    }

    /// Change the journal; a change is written to the character's file at
    /// once.
    pub fn journal_command(&mut self, command: &JournalCommand, now_unix_ms: i64) {
        let journal = &mut self.journal;
        match command {
            JournalCommand::NewPage => journal.new_page(),
            JournalCommand::DeletePage(page) => {
                journal.delete_page(*page);
            }
            JournalCommand::GotoPage(page) => {
                journal.goto_page(*page);
            }
            JournalCommand::Edit {
                label,
                title,
                notes,
            } => journal.update_current(label, title, notes),
            JournalCommand::RecordLocation { x, y } => journal.record_location(*x, *y),
            JournalCommand::SetTimer {
                days,
                hours,
                minutes,
            } => journal.set_timer(*days, *hours, *minutes),
            JournalCommand::StartTimer => {
                journal.start_timer(now_unix_ms);
            }
            JournalCommand::ResetTimer => journal.reset_timer(),
        }
        if let Some(file) = &self.journal_file
            && let Err(message) = file.save(&mut self.journal, now_unix_ms)
        {
            self.report(now_unix_ms, &message);
        }
    }

    /// The character left the world: the journal is saved and every piece
    /// of server-owned state is forgotten. The transcript stays.
    pub fn left_world(&mut self, now_unix_ms: i64) {
        if let Some(file) = &mut self.journal_file
            && let Err(message) = file.close(&mut self.journal, now_unix_ms)
        {
            self.report(now_unix_ms, &message);
        }
        self.player_guid = 0;
        self.journal.reset_session();
        self.targets.reset_session();
        self.turbine.reset();
        self.friends.clear();
        self.squelch.clear();
        self.contracts.reset_session();
        self.chat.reset_session_identity();
        self.pending_confirmations.clear();
        self.server_confirmation = None;
        self.local_requests.clear();
        self.window_requests.clear();
    }

    // -- Inbound ------------------------------------------------------------

    /// Apply one parsed server message; messages that are not about chat or
    /// social state are ignored.
    pub fn apply(&mut self, message: &Inbound<'_>, now_unix_ms: i64) {
        let now = now_unix_ms;
        match message {
            Inbound::HearSpeech(speech) => {
                let entry = self
                    .chat
                    .on_local_speech(
                        now,
                        &speech.sender_name,
                        &speech.text,
                        speech.sender_guid,
                        speech.is_ranged(),
                        speech.chat_type,
                    )
                    .cloned();
                self.commit(entry);
            }
            Inbound::ServerMessage(server) => {
                self.add_text(now, &server.message, server.chat_type);
            }
            Inbound::EmoteText(emote) => {
                let entry = self
                    .chat
                    .on_emote(now, &emote.sender_name, &emote.text, emote.sender_guid)
                    .cloned();
                self.commit(entry);
            }
            // The player's own soul emote coming back is not shown: the
            // pose already printed the player's line.
            Inbound::SoulEmote(emote) if emote.sender_guid == self.player_guid => {}
            Inbound::SoulEmote(emote) => {
                let entry = self
                    .chat
                    .on_soul_emote(now, &emote.sender_name, &emote.text, emote.sender_guid)
                    .cloned();
                self.commit(entry);
            }
            Inbound::PlayerKilled(killed) => {
                let local = self.local_player_guid();
                let entry = self
                    .chat
                    .on_player_killed(
                        now,
                        &killed.death_message,
                        killed.victim_guid,
                        killed.killer_guid,
                        local,
                    )
                    .cloned();
                self.commit(entry);
            }
            Inbound::TurbineChat(turbine) => self.apply_turbine_chat(turbine, now),
            Inbound::GameEvent(event) => self.apply_game_event(event, now),
            _ => {}
        }
    }

    fn local_player_guid(&self) -> u32 {
        self.player_guid
    }

    fn apply_turbine_chat(&mut self, turbine: &TurbineChat, now: i64) {
        match &turbine.payload {
            Payload::EventSendToRoom {
                room_id,
                sender_name,
                message,
                chat_type,
                ..
            } => {
                let entry = self
                    .chat
                    .on_channel_broadcast(
                        now,
                        *room_id,
                        sender_name,
                        message,
                        Some(turbine::log_text_type(*chat_type)),
                        &turbine::display_name(*room_id, *chat_type),
                    )
                    .cloned();
                self.commit(entry);
            }
            Payload::Response { hresult, .. } if *hresult != 0 => {
                #[allow(clippy::cast_sign_loss)]
                let text = format!(
                    "TurbineChat send rejected (hresult=0x{:08X}).",
                    *hresult as u32
                );
                let entry = self
                    .chat
                    .on_system_message(now, &text, LogTextType::Default.as_u32())
                    .cloned();
                self.commit(entry);
            }
            _ => {}
        }
    }

    fn apply_game_event(&mut self, event: &GameEventEnvelope<'_>, now: i64) {
        let result = self.route_game_event(event, now);
        if result.is_err() {
            *self
                .parse_failures
                .entry(0xF7B0_0000 | event.event_type.0)
                .or_default() += 1;
        }
    }

    fn route_game_event(
        &mut self,
        event: &GameEventEnvelope<'_>,
        now: i64,
    ) -> Result<(), ProtoError> {
        let payload = event.payload;
        match event.event_type {
            GameEventType::CHANNEL_BROADCAST => {
                let p = ChannelBroadcast::parse(payload)?;
                let entry = self
                    .chat
                    .on_channel_broadcast(now, p.channel_id, &p.sender_name, &p.message, None, "")
                    .cloned();
                self.commit(entry);
            }
            GameEventType::TELL => {
                let p = Tell::parse(payload)?;
                let entry = self
                    .chat
                    .on_tell_received(
                        now,
                        &p.sender_name,
                        &p.message,
                        p.sender_guid,
                        p.chat_type,
                        p.target_guid,
                    )
                    .cloned();
                self.commit(entry);
            }
            GameEventType::COMMUNICATION_TRANSIENT_STRING => {
                let text = wire_chat::parse_string_event(payload)?;
                self.add_text(now, &text, LogTextType::ClientLocal.as_u32());
            }
            GameEventType::POPUP_STRING => {
                let text = wire_chat::parse_string_event(payload)?;
                let entry = self.chat.on_popup(now, &text).cloned();
                self.commit(entry);
            }
            GameEventType::QUERY_AGE_RESPONSE => {
                let p = QueryAgeResponse::parse(payload)?;
                let text = if p.name.is_empty() {
                    format!("You have played for {}.", p.age)
                } else {
                    format!("{} has played for {}.", p.name, p.age)
                };
                self.system_message(now, &text);
            }
            GameEventType::AVAILABLE_HOUSES => {
                let houses = ac_proto::house::AvailableHouses::parse(payload)?;
                for line in crate::gameplay::house::available_houses_lines(&houses) {
                    self.system_message(now, &line);
                }
            }
            GameEventType::ALLEGIANCE_INFO_RESPONSE => {
                let info = ac_proto::allegiance::AllegianceInfoResponse::parse(payload)?;
                for line in crate::gameplay::allegiance::info_lines(info.target_guid, &info.profile)
                {
                    self.system_message(now, &line);
                }
            }
            GameEventType::CHANNEL_INDEX => {
                let channels = wire_chat::parse_name_list(payload)?;
                self.system_message(now, "The following channels are available to you:");
                for channel in channels {
                    self.system_message(now, &channel);
                }
            }
            GameEventType::CHANNEL_LIST => {
                let names = wire_chat::parse_name_list(payload)?;
                self.system_message(
                    now,
                    "The following characters are currently listening on the channel:",
                );
                for name in names {
                    self.system_message(now, &name);
                }
            }
            GameEventType::FRIENDS_LIST_UPDATE => {
                let update = ac_proto::social::FriendsUpdate::parse(payload)?;
                self.friends.apply(&friends_update(update));
            }
            GameEventType::SET_SQUELCH_DB => {
                let database = ac_proto::social::SquelchDatabase::parse(payload)?;
                self.squelch.replace(squelch_database(database));
            }
            GameEventType::SET_TURBINE_CHAT_CHANNELS => {
                let c = TurbineChatChannels::parse(payload)?;
                self.turbine.on_channels_received(TurbineChatRooms {
                    allegiance: c.allegiance,
                    general: c.general,
                    trade: c.trade,
                    lfg: c.lfg,
                    roleplay: c.roleplay,
                    olthoi: c.olthoi,
                    society: c.society,
                    society_celestial_hand: c.society_celestial_hand,
                    society_eldrytch_web: c.society_eldrytch_web,
                    society_radiant_blood: c.society_radiant_blood,
                });
            }
            GameEventType::WEENIE_ERROR => {
                let code = wire_chat::parse_weenie_error(payload)?;
                self.show_weenie_error(now, code, None);
            }
            GameEventType::WEENIE_ERROR_WITH_STRING => {
                let p = wire_chat::WeenieErrorWithString::parse(payload)?;
                self.show_weenie_error(now, p.error_code, Some(&p.interpolation));
            }
            GameEventType::SEND_CLIENT_CONTRACT_TRACKER_TABLE => {
                let table = ac_proto::contract::parse_contract_table(payload)?;
                self.contracts.apply_table(&table, now);
            }
            GameEventType::SEND_CLIENT_CONTRACT_TRACKER => {
                let update = ac_proto::contract::ContractTrackerUpdate::parse(payload)?;
                self.contracts.apply_update(&update, now);
            }
            _ => {}
        }
        Ok(())
    }

    /// Client-local text for the status overlay (the inventory's refusals).
    pub fn show_interface_text(&mut self, now_unix_ms: i64, text: &str) {
        self.add_text(now_unix_ms, text, LogTextType::ClientLocal.as_u32());
    }

    /// A failure code, worded and routed by its text class.
    pub fn show_weenie_error_code(&mut self, now_unix_ms: i64, code: u32) {
        self.show_weenie_error(now_unix_ms, code, None);
    }

    /// Another owner's text in its text class (client-local text goes to
    /// the overlay, anything else to the transcript).
    pub fn show_text(&mut self, now_unix_ms: i64, text: &str, log_text_type: LogTextType) {
        self.add_text(now_unix_ms, text, log_text_type.as_u32());
    }

    /// A line a plugin posted, in its text class: only its
    /// `<COORD:World:pair>` tags are read (links in the transcript, their
    /// captions alone on the status line); any other angle-bracket text is
    /// shown as written.
    pub fn show_plugin_text(&mut self, now_unix_ms: i64, text: &str, log_text_type: LogTextType) {
        self.add_text_marked(now_unix_ms, text, log_text_type.as_u32(), true);
    }

    /// A combat line (a death message).
    pub fn show_combat_line(&mut self, now_unix_ms: i64, text: &str, kind: CombatLineKind) {
        let entry = self
            .chat
            .on_combat_line(now_unix_ms, text, LogTextType::Default.as_u32(), kind)
            .cloned();
        self.commit(entry);
    }

    /// A combat notification worded as its chat line.
    pub fn show_combat_notice(&mut self, now_unix_ms: i64, notice: &ac_core::chat::CombatNotice) {
        let entry = self.chat.on_combat_notice(now_unix_ms, notice).cloned();
        self.commit(entry);
    }

    // -- Lines ----------------------------------------------------------------

    fn commit(&mut self, entry: Option<ChatEntry>) {
        if let Some(entry) = entry {
            self.committed(entry);
        }
        // A line the transcript held for the plugins' filters waits here,
        // in order with held status text.
        self.held
            .extend(self.chat.take_held().into_iter().map(HeldText::Line));
    }

    // -- Plugin chat filters ------------------------------------------------

    /// The session's plugins filter chat (`true`, OpenAC
    /// `ChatSuppressionFilters` consulted in `ChatLog.Append`): every line
    /// the transcript would keep, and every status notice, waits for them
    /// ([`Self::take_held`]) and is kept or dropped with [`Self::release`],
    /// so a dropped line reaches neither the transcript, its log, its
    /// listeners nor the reply targets. Turned off, what waits is kept: no
    /// filter is left to drop it.
    pub fn set_plugin_filtering(&mut self, on: bool) {
        if self.plugin_filtering == on {
            return;
        }
        self.plugin_filtering = on;
        self.chat.set_holding(on);
        if !on {
            for held in self.take_held() {
                self.release(held, false);
            }
        }
    }

    /// Whether the plugins filter chat now.
    #[must_use]
    pub fn plugin_filtering(&self) -> bool {
        self.plugin_filtering
    }

    /// The text waiting for the plugins' filters, oldest first.
    pub fn take_held(&mut self) -> Vec<HeldText> {
        self.held
            .extend(self.chat.take_held().into_iter().map(HeldText::Line));
        std::mem::take(&mut self.held)
    }

    /// The filters' verdict on held text: kept, a line is appended (then
    /// it teaches the reply targets and reaches the log and the host) and
    /// a status notice shows; suppressed, a line goes nowhere and a status
    /// notice only to the plugins ([`CommunicationEvent::StatusNotice`]).
    pub fn release(&mut self, held: HeldText, suppressed: bool) {
        match held {
            HeldText::Line(_) if suppressed => {}
            HeldText::Line(entry) => {
                let entry = self.chat.admit(entry).clone();
                self.committed(entry);
            }
            HeldText::Status { text, .. } => self.status_notice(text, !suppressed),
        }
    }

    /// A notice took the status path: the plugins hear it (OpenAC
    /// `IEvents.StatusTextReceived`, raised whatever the filters decide),
    /// and it shows when `shown`.
    fn status_notice(&mut self, text: String, shown: bool) {
        self.status_notices += 1;
        self.events.push(CommunicationEvent::StatusNotice {
            sequence: self.status_notices,
            text: text.clone(),
        });
        if shown {
            self.show_status(text);
        }
    }

    fn show_status(&mut self, text: String) {
        self.spew_box.enqueue(&text);
        self.events.push(CommunicationEvent::InterfaceText(text));
    }

    /// A line the transcript accepted: it teaches the reply targets, goes to
    /// the `@log` file and to the host.
    fn committed(&mut self, entry: ChatEntry) {
        self.targets.observe(&entry);
        let utc_offset = self.time_zone.utc_offset_seconds(entry.received_unix_ms);
        let line = ChatLine::from_entry(&entry, self.display_timestamps, utc_offset);
        if let Some(log) = &mut self.log_file
            && log.is_open()
        {
            let prefix = self.display_timestamps.then(|| {
                ac_core::chat::format_timestamp_prefix(entry.received_unix_ms, utc_offset)
            });
            // The file gets the line as composed, before the chat window
            // reads its tags: a speaker's name inside its tell tag, any tag
            // in the words as it arrived. A plugin's own link markup is ours
            // rather than the chat window's; its line is written as shown.
            let text = if entry.allows_markup {
                format(&entry)
            } else {
                format_tagged(&entry)
            };
            log.write(prefix.as_deref(), &text);
        }
        self.events.push(CommunicationEvent::Line(line));
    }

    fn system_message(&mut self, now: i64, text: &str) {
        let entry = self.chat.on_system_message(now, text, 0).cloned();
        self.commit(entry);
    }

    /// Journal problems go into the transcript in the help class, as the
    /// journal panel reports them.
    fn report(&mut self, now: i64, text: &str) {
        let entry = self
            .chat
            .on_system_message(now, text, LogTextType::Help.as_u32())
            .cloned();
        self.commit(entry);
    }

    /// Text by class: client-local text goes to the status overlay, every
    /// other class into the transcript as a system line.
    fn add_text(&mut self, now: i64, text: &str, log_text_type: u32) {
        self.add_text_marked(now, text, log_text_type, false);
    }

    /// [`Self::add_text`], for a line a plugin posted when `allows_markup`:
    /// only its `<COORD:World:pair>` tags are read, as links in the
    /// transcript; any other angle-bracket text is shown as written. The
    /// status line has no links, so there the tags are removed and only
    /// their captions shown.
    fn add_text_marked(&mut self, now: i64, text: &str, log_text_type: u32, allows_markup: bool) {
        let text = text.trim();
        if log_text_type == LogTextType::ClientLocal.as_u32() {
            // The status line shows text, not links: a plugin's tags would
            // otherwise appear there as raw markup.
            let plain;
            let text = if allows_markup {
                plain = ac_core::chat::tag_markup::to_plain_text(text);
                plain.as_str()
            } else {
                text
            };
            // A status notice never enters the transcript, so the plugins'
            // filters are offered it on its own (OpenAC
            // `RuntimeCommunicationState.AddText`).
            // The plugins hear it when the filters have been asked, in
            // order with the held lines, whatever they decide.
            if self.plugin_filtering {
                self.held.push(HeldText::Status {
                    text: text.to_owned(),
                    received_unix_ms: now,
                });
            } else {
                self.status_notice(text.to_owned(), true);
            }
            return;
        }
        let entry = if allows_markup {
            self.chat.on_plugin_message(now, text, log_text_type)
        } else {
            self.chat.on_system_message(now, text, log_text_type)
        }
        .cloned();
        self.commit(entry);
    }

    fn show_weenie_error(&mut self, now: i64, code: u32, param: Option<&str>) {
        if weenie_errors::is_silent_client_control_status(code) {
            return;
        }
        if let Some((text, kind)) = weenie_errors::resolve(code, param) {
            self.add_text(now, &text, kind.as_u32());
        }
    }
}

/// The wire's friends update as the friends list takes it.
fn friends_update(update: ac_proto::social::FriendsUpdate) -> FriendsUpdate {
    FriendsUpdate {
        update_type: FriendsUpdateType(update.update_type),
        entries: update
            .entries
            .into_iter()
            .map(|e| FriendEntry {
                id: e.id,
                name: e.name,
                online: e.online,
                appear_offline: e.appear_offline,
                friends: e.friends,
                friend_of: e.friend_of,
            })
            .collect(),
    }
}

/// The wire's squelch database as the squelch state takes it.
fn squelch_database(database: ac_proto::social::SquelchDatabase) -> SquelchDatabase {
    let info = |i: ac_proto::social::SquelchInfo| SquelchInfo {
        name: i.name,
        account_wide: i.account_wide,
        message_types: i.message_types.into_iter().collect(),
    };
    SquelchDatabase {
        accounts: database.accounts.into_iter().collect(),
        characters: database
            .characters
            .into_iter()
            .map(|(id, i)| (id, info(i)))
            .collect(),
        global: info(database.global),
    }
}
