//! The player's side: typed lines, chat lines, client commands and the
//! requests they send.

use ac_core::chat::{ChatKind, LogTextType, TurbineChannel, legacy_channel_chat_type};
use ac_core::social::{FriendsState, SquelchDatabase};
use ac_proto::ProtoError;
use ac_proto::{allegiance, chat as wire, client_commands as cc, house, social};

use super::administration::AdministrationRequest;
use super::catalog::ClientCommandId;
use super::dispatcher::{
    self, ClientCommandHost, ClientConfirmation, ClientRequest, LocalRequest, NEEDS_A_WINDOW,
    WindowRequest,
};
use super::input::{self, ChatChannelKind};
use super::log_file::ChatLogResult;
use super::router::{self, ChatFeedback, Defaults, RoutedCommand, SendChat, SubmitOutcome};
use super::{Communication, PlayerFacts, turbine};
use crate::character::CharacterOptions;
use crate::session::{Outbox, Queue};

/// Where requests go and what the commands may read about the player.
#[derive(Debug)]
pub struct Outbound<'a> {
    /// The session's outgoing queue.
    pub outbox: &'a mut Outbox,
    /// The player's facts.
    pub player: PlayerFacts,
    /// Now, UTC milliseconds since the Unix epoch.
    pub now_unix_ms: i64,
    /// True in the world. Outside it nothing is sent, and a tell records no
    /// retell target.
    pub active: bool,
    /// The character options (`/join`, `/consent on`, `/day` change them).
    pub options: &'a mut CharacterOptions,
}

/// The owner and the outgoing side together: the router's feedback and the
/// dispatcher's host.
struct Context<'c, 'o> {
    communication: &'c mut Communication,
    out: &'c mut Outbound<'o>,
}

impl ChatFeedback for Context<'_, '_> {
    fn show_interface_text(&mut self, text: &str) {
        self.communication.add_text(
            self.out.now_unix_ms,
            text,
            LogTextType::ClientLocal.as_u32(),
        );
    }

    fn show_system_message(&mut self, text: &str) {
        self.communication
            .system_message(self.out.now_unix_ms, text);
    }

    fn last_incoming_tell_sender(&self) -> Option<&str> {
        self.communication.targets.last_incoming_tell_sender()
    }

    fn last_outgoing_tell_target(&self) -> Option<&str> {
        self.communication.targets.last_outgoing_tell_target()
    }
}

impl ClientCommandHost for Context<'_, '_> {
    fn send(&mut self, request: ClientRequest) {
        self.communication.send_request(request, self.out);
    }

    fn show_system_message(&mut self, text: &str) {
        ChatFeedback::show_system_message(self, text);
    }

    fn show_client_local(&mut self, text: &str) {
        ChatFeedback::show_interface_text(self, text);
    }

    fn show_weenie_error(&mut self, code: u32) {
        self.communication
            .show_weenie_error(self.out.now_unix_ms, code, None);
    }

    fn player_public_weenie_bitfield(&self) -> Option<u32> {
        self.out.player.public_weenie_bitfield
    }

    fn is_away(&self) -> bool {
        self.out.player.is_away
    }

    fn clear_chat(&mut self, _all: bool) {
        // Every chat window shows the one transcript.
        self.communication.chat.clear();
    }

    fn set_chat_log_file(&mut self, name: &str) -> ChatLogResult {
        match &mut self.communication.log_file {
            Some(log) => log.set(name),
            None => ChatLogResult {
                opened: false,
                closed: false,
                name: name.to_owned(),
                closed_name: None,
            },
        }
    }

    fn friends(&self) -> &FriendsState {
        &self.communication.friends
    }

    fn clear_friends_locally(&mut self) {
        self.communication.friends.clear();
    }

    fn squelch(&self) -> &SquelchDatabase {
        self.communication.squelch.database()
    }

    fn last_teller(&self) -> Option<&str> {
        self.communication.targets.last_incoming_tell_sender()
    }

    fn is_using_turbine_chat(&self) -> bool {
        self.communication.turbine.enabled()
    }

    fn client_version(&self) -> &str {
        &self.communication.client_version
    }

    fn character_option(&self, option_id: u32) -> bool {
        self.out.options.get(option_id)
    }

    fn last_outside_corpse_cell(&self) -> Option<u32> {
        self.out.player.last_outside_corpse_cell
    }

    fn current_position(&self) -> Option<(u32, [f32; 3], [f32; 4])> {
        self.out.player.current_position
    }

    fn ask_confirmation(&mut self, confirmation: ClientConfirmation) {
        // One question open at a time, as one dialog: a later one waits.
        self.communication.queue_confirmation(confirmation);
    }

    fn has_open_vendor(&self) -> bool {
        self.out.player.has_open_vendor
    }

    fn request(&mut self, request: LocalRequest) {
        self.communication.local_requests.push(request);
    }

    fn window(&mut self, request: WindowRequest) {
        if self.out.player.has_window {
            self.communication.window_requests.push(request);
        } else {
            ChatFeedback::show_system_message(self, NEEDS_A_WINDOW);
        }
    }

    fn set_character_option(&mut self, option_id: u32, value: bool) {
        let out = &mut *self.out;
        let now = std::time::Duration::from_millis(u64::try_from(out.now_unix_ms).unwrap_or(0));
        let (active, outbox) = (out.active, &mut *out.outbox);
        out.options
            .try_set(option_id, value, now, &mut |id, value| {
                if active {
                    outbox.send_action(|seq| {
                        social::build_set_single_character_option(seq, id, value)
                    });
                }
            });
    }
}

impl Communication {
    /// A line the player typed, routed as the chat box routes it.
    pub fn submit(
        &mut self,
        line: &str,
        defaults: Defaults<'_>,
        out: &mut Outbound<'_>,
    ) -> SubmitOutcome {
        let mut context = Context {
            communication: self,
            out,
        };
        let (outcome, routed) = router::submit(line, &mut context, defaults);
        if let Some(routed) = routed {
            self.dispatch(routed, out);
        }
        outcome
    }

    /// Hand on what the router decided.
    pub fn dispatch(&mut self, routed: RoutedCommand, out: &mut Outbound<'_>) {
        match routed {
            RoutedCommand::Client { command, arguments } => {
                self.execute(command, &arguments, out);
            }
            RoutedCommand::ServerCommand(text) => {
                if !text.is_empty() {
                    self.send_action(out, |seq| wire::build_talk(seq, &text));
                }
            }
            RoutedCommand::Chat(chat) => self.send_chat(chat, out),
            RoutedCommand::RawChannel { channel_id, text } => {
                self.send_action(out, |seq| wire::build_chat_channel(seq, channel_id, &text));
            }
        }
    }

    /// Run one client command.
    pub fn execute(&mut self, command: ClientCommandId, arguments: &str, out: &mut Outbound<'_>) {
        let mut context = Context {
            communication: self,
            out,
        };
        dispatcher::execute(command, arguments, &mut context);
    }

    /// Change one character option the way the options panel does; an
    /// unknown id changes nothing.
    pub fn set_character_option(&mut self, option_id: u32, value: bool, out: &mut Outbound<'_>) {
        let mut context = Context {
            communication: self,
            out,
        };
        ClientCommandHost::set_character_option(&mut context, option_id, value);
    }

    /// Send a chat line where it says.
    pub fn send_chat(&mut self, chat: SendChat, out: &mut Outbound<'_>) {
        if chat.text.is_empty() {
            return;
        }
        match chat.channel {
            ChatChannelKind::Say => {
                // A pose in speech (`hello *wave*`) plays its motion, tells
                // onlookers and the player, and leaves the line.
                let poses = std::sync::Arc::clone(&self.chat_poses);
                let male = out.player.male;
                let mut played = Vec::new();
                let spoken = super::extract_poses(
                    &chat.text,
                    |command| poses.resolve(command, male),
                    |pose| played.push(pose),
                );
                for pose in played {
                    self.pose_motions.push(pose.motion_command);
                    if !pose.others_text.is_empty() {
                        let text = pose.others_text.as_str();
                        self.send_action(out, |seq| wire::build_soul_emote(seq, text));
                    }
                    if !pose.self_text.is_empty() {
                        let entry = self
                            .chat
                            .on_soul_emote(out.now_unix_ms, "You", &pose.self_text, 0)
                            .cloned();
                        self.commit(entry);
                    }
                }
                if !spoken.is_empty() {
                    self.send_action(out, |seq| wire::build_talk(seq, &spoken));
                }
            }
            ChatChannelKind::Tell => self.send_tell(&chat, out),
            ChatChannelKind::Allegiance if !self.turbine.enabled() => {
                self.send_legacy_channel(ChatChannelKind::AllegianceBroadcast, &chat.text, out);
            }
            kind => match turbine_channel(kind) {
                Some(channel) => self.send_turbine(channel, &chat.text, out),
                None => self.send_legacy_channel(kind, &chat.text, out),
            },
        }
    }

    fn send_tell(&mut self, chat: &SendChat, out: &mut Outbound<'_>) {
        // An addressee picked in the world is aimed at by id, which reaches
        // creatures a name lookup cannot; it leaves the retell target alone.
        if chat.target_guid != 0 {
            let (guid, text) = (chat.target_guid, chat.text.as_str());
            self.send_action(out, |seq| wire::build_talk_direct(seq, guid, text));
            return;
        }
        let Some(target) = chat.target_name.as_deref().filter(|t| !t.is_empty()) else {
            return;
        };
        if self.send_action(out, |seq| wire::build_tell(seq, target, &chat.text)) {
            // The transcript line for our own tell comes back from the
            // server without the addressee, so the send records it.
            self.targets.note_outgoing_tell(target);
        }
    }

    fn send_turbine(&mut self, channel: TurbineChannel, text: &str, out: &mut Outbound<'_>) {
        let gate = turbine::evaluate(channel, &self.turbine, out.player.membership);
        if gate.status != turbine::GateStatus::Allowed {
            if let Some((refusal, kind)) = turbine::refusal_text(&gate) {
                self.add_text(out.now_unix_ms, &refusal, kind.as_u32());
            }
            return;
        }
        if !out.active {
            return;
        }
        let cookie = self.turbine.next_context_id();
        match ac_proto::turbine_chat::build_turbine_chat_to_room(
            gate.room_id,
            gate.chat_type,
            out.player.guid,
            text,
            cookie,
        ) {
            Ok(body) => out.outbox.send(body, Queue::Ui),
            Err(error) => self.refused(out.now_unix_ms, &error),
        }
    }

    fn send_legacy_channel(&mut self, kind: ChatChannelKind, text: &str, out: &mut Outbound<'_>) {
        let Some((channel_id, display_name)) = input::legacy_channel(kind) else {
            return;
        };
        if !self.send_action(out, |seq| wire::build_chat_channel(seq, channel_id, text)) {
            return;
        }
        let echoes = ac_core::chat::ChatChannelInfo::Legacy {
            channel_id,
            display_name: display_name.to_owned(),
        }
        .is_self_echo_channel();
        if !echoes {
            let entry = self
                .chat
                .on_self_sent(
                    out.now_unix_ms,
                    ChatKind::Channel,
                    text,
                    legacy_channel_chat_type(channel_id, true).as_u32(),
                    "",
                    channel_id,
                )
                .cloned();
            self.commit(entry);
        }
    }

    /// Send one client request.
    pub fn send_request(&mut self, request: ClientRequest, out: &mut Outbound<'_>) {
        use ClientRequest as R;
        match request {
            R::TeleportToLifestone => self.send_plain(out, cc::build_tele_to_lifestone),
            R::TeleportToMarketplace => self.send_plain(out, cc::build_tele_to_marketplace),
            R::TeleportToPkArena => self.send_plain(out, cc::build_tele_to_pk_arena),
            R::TeleportToPkLiteArena => self.send_plain(out, cc::build_tele_to_pk_lite_arena),
            R::TeleportToHouse => self.send_plain(out, cc::build_tele_to_house),
            R::TeleportToMansion => self.send_plain(out, cc::build_tele_to_mansion),
            R::RecallAllegianceHometown => {
                self.send_plain(out, cc::build_recall_allegiance_hometown);
            }
            R::EnterPkLite => self.send_plain(out, cc::build_enter_pk_lite),
            // Object id 0 asks about the player.
            R::QueryAge => self.send_plain(out, |seq| cc::build_query_age(seq, 0)),
            R::QueryBirth => self.send_plain(out, |seq| cc::build_query_birth(seq, 0)),
            R::SetAfkMode(away) => self.send_plain(out, |seq| cc::build_set_afk_mode(seq, away)),
            R::SetAfkMessage(message) => {
                self.send_action(out, |seq| cc::build_set_afk_message(seq, &message));
            }
            R::DisplayConsent => self.send_plain(out, cc::build_display_consent),
            R::ClearConsent => self.send_plain(out, cc::build_clear_consent),
            R::RemoveConsent(name) => {
                self.send_action(out, |seq| cc::build_remove_consent(seq, &name));
            }
            R::Emote(text) => {
                self.send_action(out, |seq| wire::build_emote(seq, &text));
            }
            R::AddFriend(name) => {
                self.send_action(out, |seq| social::build_add_friend(seq, &name));
            }
            R::RemoveFriend(id) => self.send_plain(out, |seq| social::build_remove_friend(seq, id)),
            R::ClearFriends => self.send_plain(out, social::build_clear_friends),
            R::LegacyFriendsList => {
                if out.active {
                    out.outbox
                        .send(social::build_legacy_friends_list_request(), Queue::Control);
                }
            }
            R::CharacterSquelch {
                add,
                character_id,
                name,
                message_type,
            } => {
                self.send_action(out, |seq| {
                    social::build_modify_character_squelch(
                        seq,
                        add,
                        character_id,
                        &name,
                        message_type,
                    )
                });
            }
            R::AccountSquelch { add, name } => {
                self.send_action(out, |seq| {
                    social::build_modify_account_squelch(seq, add, &name)
                });
            }
            R::GlobalSquelch { add, message_type } => self.send_plain(out, |seq| {
                social::build_modify_global_squelch(seq, add, message_type)
            }),
            R::IndexChannels => self.send_plain(out, cc::build_index_channels),
            R::ListChannel(id) => self.send_plain(out, |seq| cc::build_list_channel(seq, id)),
            R::OnChannel(id) => self.send_plain(out, |seq| cc::build_add_channel(seq, id)),
            R::OffChannel(id) => self.send_plain(out, |seq| cc::build_remove_channel(seq, id)),
            R::AddPlayerPermission(name) => {
                self.send_action(out, |seq| house::build_add_player_permission(seq, &name));
            }
            R::RemovePlayerPermission(name) => {
                self.send_action(out, |seq| house::build_remove_player_permission(seq, &name));
            }
            R::ListAvailableHouses(house_type) => self.send_plain(out, |seq| {
                house::build_list_available_houses(seq, house_type)
            }),
            R::AllegianceInfo(name) => {
                self.send_action(out, |seq| allegiance::build_info_request(seq, &name));
            }
            R::Administration(request) => self.send_administration(request, out),
            R::Suicide => self.send_plain(out, cc::build_suicide),
        }
    }

    /// The player answered a question: an answer to a question not asked
    /// (or already answered) does nothing; a yes to `/die` sends the
    /// suicide; the server's question gets the answer either way, with its
    /// kind and id.
    pub fn answer_confirmation(
        &mut self,
        confirmation: ClientConfirmation,
        accepted: bool,
        out: &mut Outbound<'_>,
    ) {
        if let ClientConfirmation::Server {
            confirmation_type,
            context_id,
            ..
        } = confirmation
        {
            let open = matches!(
                &self.server_confirmation,
                Some(ClientConfirmation::Server { context_id: c, .. }) if *c == context_id
            );
            if open {
                self.server_confirmation = None;
                self.send_plain(out, |seq| {
                    ac_proto::social::build_confirmation_response(
                        seq,
                        confirmation_type,
                        context_id,
                        accepted,
                    )
                });
            }
            return;
        }
        if self.pending_confirmations.front() != Some(&confirmation) {
            return;
        }
        self.pending_confirmations.pop_front();
        if accepted {
            match confirmation {
                ClientConfirmation::Suicide => self.send_request(ClientRequest::Suicide, out),
                // Abandoning a house is asked twice; the second question,
                // made from the answer, opens before those waiting.
                ClientConfirmation::AbandonHouse => {
                    self.pending_confirmations
                        .push_front(ClientConfirmation::AbandonHouseCertain);
                }
                ClientConfirmation::AbandonHouseCertain => {
                    self.send_plain(out, ac_proto::house::build_abandon_house);
                }
                ClientConfirmation::ItemUse { object_id, .. } => {
                    self.local_requests
                        .push(LocalRequest::ConfirmedUse(object_id));
                }
                ClientConfirmation::ManaStoneDrain { stone, target, .. } => {
                    self.local_requests
                        .push(LocalRequest::ConfirmedManaStoneDrain { stone, target });
                }
                ClientConfirmation::Server { .. } => {}
            }
        }
        // The next question in line opens.
        self.open_next_confirmation();
    }

    /// Send one allegiance or house administration request.
    fn send_administration(&mut self, request: AdministrationRequest, out: &mut Outbound<'_>) {
        use AdministrationRequest as A;
        match request {
            A::BreakAllegianceBoot { name, account } => {
                self.send_action(out, |seq| {
                    allegiance::build_break_allegiance_boot(seq, &name, account)
                });
            }
            A::AllegianceChatBoot { name, reason } => {
                self.send_action(out, |seq| allegiance::build_chat_boot(seq, &name, &reason));
            }
            A::AllegianceChatGag { name, enabled } => {
                self.send_action(out, |seq| allegiance::build_chat_gag(seq, &name, enabled));
            }
            A::AllegianceBroadcast(text) => {
                // The allegiance broadcast channel, sent as is (no local line).
                self.send_action(out, |seq| {
                    wire::build_chat_channel(seq, ALLEGIANCE_BROADCAST_CHANNEL, &text)
                });
            }
            A::ListAllegianceBans => self.send_plain(out, allegiance::build_list_bans),
            A::AddAllegianceBan(name) => {
                self.send_action(out, |seq| allegiance::build_add_ban(seq, &name));
            }
            A::RemoveAllegianceBan(name) => {
                self.send_action(out, |seq| allegiance::build_remove_ban(seq, &name));
            }
            A::ListAllegianceOfficers => self.send_plain(out, allegiance::build_list_officers),
            A::ClearAllegianceOfficers => self.send_plain(out, allegiance::build_clear_officers),
            A::SetAllegianceOfficer { name, level } => {
                self.send_action(out, |seq| allegiance::build_set_officer(seq, &name, level));
            }
            A::RemoveAllegianceOfficer(name) => {
                self.send_action(out, |seq| allegiance::build_remove_officer(seq, &name));
            }
            A::ListAllegianceOfficerTitles => {
                self.send_plain(out, allegiance::build_list_officer_titles);
            }
            A::ClearAllegianceOfficerTitles => {
                self.send_plain(out, allegiance::build_clear_officer_titles);
            }
            A::SetAllegianceOfficerTitle { level, title } => {
                self.send_action(out, |seq| {
                    allegiance::build_set_officer_title(seq, level, &title)
                });
            }
            A::QueryAllegianceName => self.send_plain(out, allegiance::build_query_name),
            A::SetAllegianceName(name) => {
                self.send_action(out, |seq| allegiance::build_set_name(seq, &name));
            }
            A::ClearAllegianceName => self.send_plain(out, allegiance::build_clear_name),
            A::AllegianceLockAction(action) => {
                self.send_plain(out, |seq| allegiance::build_lock_action(seq, action));
            }
            A::SetAllegianceApprovedVassal(name) => {
                self.send_action(out, |seq| allegiance::build_set_approved_vassal(seq, &name));
            }
            A::AllegianceHouseAction(action) => {
                self.send_plain(out, |seq| allegiance::build_house_action(seq, action));
            }
            A::QueryMotd => self.send_plain(out, allegiance::build_query_motd),
            A::SetMotd(text) => {
                self.send_action(out, |seq| allegiance::build_set_motd(seq, &text));
            }
            A::ClearMotd => self.send_plain(out, allegiance::build_clear_motd),
            A::SetOpenHouseStatus(open) => {
                self.send_plain(out, |seq| house::build_set_open_house_status(seq, open));
            }
            A::AddPermanentGuest(name) => {
                self.send_action(out, |seq| house::build_add_permanent_guest(seq, &name));
            }
            A::RemovePermanentGuest(name) => {
                self.send_action(out, |seq| house::build_remove_permanent_guest(seq, &name));
            }
            A::RemoveAllPermanentGuests => {
                self.send_plain(out, house::build_remove_all_permanent_guests);
            }
            A::ChangeStoragePermission { name, enabled } => {
                self.send_action(out, |seq| {
                    house::build_change_storage_permission(seq, &name, enabled)
                });
            }
            A::AddAllStoragePermission => {
                self.send_plain(out, house::build_add_all_storage_permission);
            }
            A::RemoveAllStoragePermission => {
                self.send_plain(out, house::build_remove_all_storage_permission);
            }
            A::RequestFullGuestList => self.send_plain(out, house::build_request_full_guest_list),
            A::BootSpecificHouseGuest(name) => {
                self.send_action(out, |seq| {
                    house::build_boot_specific_house_guest(seq, &name)
                });
            }
            A::BootEveryone => self.send_plain(out, house::build_boot_everyone),
            A::SetHooksVisibility(visible) => {
                self.send_plain(out, |seq| house::build_set_hooks_visibility(seq, visible));
            }
            A::ModifyAllegianceGuestPermission(enabled) => self.send_plain(out, |seq| {
                house::build_modify_allegiance_guest_permission(seq, enabled)
            }),
            A::ModifyAllegianceStoragePermission(enabled) => self.send_plain(out, |seq| {
                house::build_modify_allegiance_storage_permission(seq, enabled)
            }),
        }
    }

    /// Abandon a contract.
    pub fn abandon_contract(&mut self, contract_id: u32, out: &mut Outbound<'_>) {
        self.send_plain(out, |seq| {
            ac_proto::contract::build_abandon_contract(seq, contract_id)
        });
    }

    /// Queue a game action whose body always builds.
    fn send_plain(&mut self, out: &mut Outbound<'_>, build: impl FnOnce(u32) -> Vec<u8>) {
        if out.active {
            out.outbox.send_action(build);
        }
    }

    /// Queue a game action whose body can fail to build (text too long for
    /// the wire); a failure is reported to the player. True when queued.
    fn send_action(
        &mut self,
        out: &mut Outbound<'_>,
        build: impl FnOnce(u32) -> Result<Vec<u8>, ProtoError>,
    ) -> bool {
        if !out.active {
            return false;
        }
        match out.outbox.try_send_action(build) {
            Ok(_) => true,
            Err(error) => {
                self.refused(out.now_unix_ms, &error);
                false
            }
        }
    }

    fn refused(&mut self, now: i64, error: &ProtoError) {
        self.system_message(now, &format!("That could not be sent: {error}."));
    }
}

/// The legacy allegiance broadcast channel.
const ALLEGIANCE_BROADCAST_CHANNEL: u32 = 0x0200_0000;

/// The chat room a channel kind speaks in, if it is one.
fn turbine_channel(kind: ChatChannelKind) -> Option<TurbineChannel> {
    Some(match kind {
        ChatChannelKind::Allegiance => TurbineChannel::Allegiance,
        ChatChannelKind::General => TurbineChannel::General,
        ChatChannelKind::Trade => TurbineChannel::Trade,
        ChatChannelKind::Lfg => TurbineChannel::Lfg,
        ChatChannelKind::Roleplay => TurbineChannel::Roleplay,
        ChatChannelKind::Society => TurbineChannel::Society,
        ChatChannelKind::Olthoi => TurbineChannel::Olthoi,
        _ => return None,
    })
}
