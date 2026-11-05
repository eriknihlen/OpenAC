//! The game-message side of a session: the data check, character
//! selection (with delete, restore and creation), entering the world,
//! login complete and log off, back to character selection or out.
//!
//! Pure logic. The caller feeds it reassembled message bodies and the
//! current time and drains the bodies it wants sent; the transport below is
//! someone else's job.

use std::collections::VecDeque;
use std::sync::Arc;
use std::time::Duration;

use ac_core::chargen::ChargenOptions;
use ac_proto::character::{
    CharGenVerificationResponse, build_character_create, build_character_delete,
    build_character_restore,
};
use ac_proto::{
    Character, CharacterError, CharacterList, Inbound, Opcode, ServerName,
    build_data_check_complete, build_enter_world, build_enter_world_request, build_house_query,
    build_interrogation_response, build_login_complete, build_logoff_request, read_opcode,
};

use super::creation::{
    CharacterCreation, ChargenRandom, CreationCommand, CreationDeltaKind, CreationSnapshot,
    SeededRandom,
};
use super::generation::Generation;
use super::outbox::{Outbox, Outgoing, Queue};
use super::selection::{CharacterSelection, SelectionEntry, SelectionSnapshot};

/// How long connecting may take, from the login request to a usable
/// character list with the data check finished.
pub const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
/// How long the server may take to say it is ready for the character.
pub const ENTER_WORLD_TIMEOUT: Duration = Duration::from_secs(10);
/// How long to wait for the server to confirm a log off. Longer than the
/// longest time the server holds a character in the world before letting go.
pub const LOGOFF_CONFIRMATION_TIMEOUT: Duration = Duration::from_secs(45);

/// Where the session is in its life.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Phase {
    /// Logging in: waiting for the character list and the data check.
    Connecting,
    /// Logged in; a character can be chosen, created, deleted or restored.
    InCharacterSelect,
    /// A character was chosen; waiting for the server to be ready.
    EnteringWorld,
    /// Playing.
    InWorld,
    /// Log off requested; waiting for the server to confirm.
    LoggingOff,
    /// The character left the world and the session ends (confirmed or
    /// timed out).
    LoggedOff,
    /// The session cannot continue.
    Failed,
}

/// Why the session failed or a request was refused.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum SessionError {
    /// Connecting did not finish in time; the text says which step stalled.
    #[error("connect timed out: {0}")]
    ConnectTimeout(&'static str),
    /// The server wants to update game data, which this client cannot apply.
    #[error("the server requires a game-data update, which is not supported")]
    DataUpdateRequired,
    /// The data check went wrong.
    #[error("game-data check failed: {0}")]
    DataCheckFailed(&'static str),
    /// No character matched the selector.
    #[error("no available character matches {0}")]
    NoCharacter(String),
    /// The server refused the character.
    #[error("the server rejected the character (error {0:#x})")]
    CharacterRejected(u32),
    /// The server ended the connection with a character error (the account
    /// logged in elsewhere, the server going down, ...); `reason` is the
    /// error's text.
    #[error("the server ended the session: {reason} (error {code:#x})")]
    ServerEnded {
        /// The character error code.
        code: u32,
        /// What the game says for it.
        reason: String,
    },
    /// The server fell silent for longer than the transport allows (it
    /// died, or dropped the session without a word).
    #[error("the connection to the server was lost")]
    ConnectionLost,
    /// The server did not get ready for the character in time.
    #[error("enter world timed out: server ready not received")]
    EnterWorldTimeout,
    /// A request came at a time it does not fit.
    #[error("{request} is not possible while {phase:?}")]
    WrongPhase {
        /// What was asked.
        request: &'static str,
        /// The phase the session was in.
        phase: Phase,
    },
    /// The character-selection or creation state refused the request.
    #[error("{0}")]
    Refused(String),
    /// A message body could not be built.
    #[error("could not build a message: {0}")]
    Encode(#[from] ac_proto::ProtoError),
}

/// Something the host may want to show or log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionEvent {
    /// The phase changed.
    PhaseChanged(Phase),
    /// The account's characters arrived.
    CharacterList(CharacterList),
    /// The world's name and population arrived.
    ServerName(ServerName),
    /// The chosen character entered the world.
    EnteredWorld(Character),
    /// The login-complete action was sent for the player's own object.
    LoginCompleteSent,
    /// The log off finished; `confirmed` is false when it timed out.
    LoggedOff {
        /// Whether the server confirmed it.
        confirmed: bool,
    },
    /// The character left the world and the session is back at character
    /// selection, in a new generation.
    ReturnedToCharacterSelect(Generation),
    /// The server created a character.
    CharacterCreated {
        /// Its object id.
        guid: u32,
        /// Its name.
        name: String,
    },
    /// The server refused a character create.
    CreationFailed {
        /// The answer code.
        code: u32,
        /// The code's name.
        reason: String,
        /// The name that was tried.
        name: String,
    },
    /// The session failed.
    Failed(SessionError),
}

/// Which character to enter the world with.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CharacterSelector {
    /// The first character that may enter the world.
    FirstAvailable,
    /// The character at this index of the active list.
    Index(usize),
    /// The character with this object id.
    Id(u32),
    /// The character with this name, ignoring case.
    Name(String),
}

/// What happens when the server confirms a log off.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum LogoffIntent {
    /// The session ends.
    Disconnect,
    /// Back to character selection on the same connection.
    ReturnToSelection,
}

/// Which request a character-generation answer belongs to (the same
/// message answers a create and a restore).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PendingChargen {
    None,
    Create,
    Restore,
}

/// The game-message state of one session.
#[derive(Debug)]
pub struct SessionFlow {
    phase: Phase,
    generation: Generation,
    deadline: Option<Duration>,
    characters: Option<CharacterList>,
    server_name: Option<ServerName>,
    data_interrogation_received: bool,
    data_check_complete: bool,
    /// Enter-world body held until the server says it is ready.
    pending_enter_world: Option<(Character, Vec<u8>)>,
    active_character: Option<Character>,
    login_complete_sent: bool,
    /// An attached window presents the login: login complete waits for it.
    hold_login_complete: bool,
    /// The player's object arrived and login complete waits for the window.
    login_complete_held: bool,
    /// The session's physics enters the character into the world: with no
    /// window presenting the login, login complete and the house query wait
    /// until it has.
    login_awaits_entry: bool,
    /// The house query waits with the held login complete.
    house_query_held: bool,
    logoff_intent: LogoffIntent,
    selection: CharacterSelection,
    creation: CharacterCreation,
    pending_chargen: PendingChargen,
    creates_since_character_list: usize,
    next_login_character_id: u32,
    selection_revision_seen: u64,
    outbox: Outbox,
    events: VecDeque<SessionEvent>,
}

impl SessionFlow {
    /// A session that has just sent its login request at `now`, in
    /// generation 1, without character-creation options.
    #[must_use]
    pub fn new(now: Duration) -> Self {
        Self::with_generation(
            now,
            Generation(1),
            Arc::default(),
            Box::new(SeededRandom::new(1)),
        )
    }

    /// A session that has just sent its login request at `now`, in
    /// `generation`; character creation chooses from `chargen`.
    #[must_use]
    pub fn with_generation(
        now: Duration,
        generation: Generation,
        chargen: Arc<ChargenOptions>,
        random: Box<dyn ChargenRandom>,
    ) -> Self {
        let mut selection = CharacterSelection::new();
        selection.begin(generation);
        let mut creation = CharacterCreation::new(chargen, random);
        creation.begin(generation);
        Self {
            phase: Phase::Connecting,
            generation,
            deadline: Some(now + CONNECT_TIMEOUT),
            characters: None,
            server_name: None,
            data_interrogation_received: false,
            data_check_complete: false,
            pending_enter_world: None,
            active_character: None,
            login_complete_sent: false,
            hold_login_complete: false,
            login_complete_held: false,
            login_awaits_entry: false,
            house_query_held: false,
            logoff_intent: LogoffIntent::Disconnect,
            selection,
            creation,
            pending_chargen: PendingChargen::None,
            creates_since_character_list: 0,
            next_login_character_id: 0,
            selection_revision_seen: 0,
            outbox: Outbox::new(),
            events: VecDeque::new(),
        }
    }

    /// Current phase.
    #[must_use]
    pub fn phase(&self) -> Phase {
        self.phase
    }

    /// The current generation (a return to character selection starts a
    /// new one).
    #[must_use]
    pub fn generation(&self) -> Generation {
        self.generation
    }

    /// The account's characters, once received.
    #[must_use]
    pub fn characters(&self) -> Option<&CharacterList> {
        self.characters.as_ref()
    }

    /// The world's name and population, once received.
    #[must_use]
    pub fn server_name(&self) -> Option<&ServerName> {
        self.server_name.as_ref()
    }

    /// The character in the world (or entering it).
    #[must_use]
    pub fn active_character(&self) -> Option<&Character> {
        self.active_character.as_ref()
    }

    /// Whether login complete was sent for the current character.
    #[must_use]
    pub fn login_complete_sent(&self) -> bool {
        self.login_complete_sent
    }

    /// Whether an attached window presents the login: when the player's
    /// object arrives the house query goes out and login complete waits
    /// for [`SessionFlow::send_held_login_complete`] (a window sends it at
    /// the end of its login presentation; without one it goes at once).
    pub fn set_hold_login_complete(&mut self, hold: bool) {
        self.hold_login_complete = hold;
    }

    /// Whether the session's physics enters the character into the world:
    /// with no window presenting the login, login complete and the house
    /// query then wait for [`SessionFlow::send_held_login_complete`], sent
    /// when the character's first entry is done (OpenAC's no-window host
    /// sends them when its first entry completes); without physics they go
    /// when the player's object arrives.
    pub fn set_login_awaits_entry(&mut self, awaits: bool) {
        self.login_awaits_entry = awaits;
    }

    /// Login complete waits for the window's login presentation or the
    /// character's entry.
    #[must_use]
    pub fn login_complete_held(&self) -> bool {
        self.login_complete_held
    }

    /// Send the login complete that waited (and the house query, when it
    /// waited with it).
    pub fn send_held_login_complete(&mut self) {
        if !self.login_complete_held {
            return;
        }
        self.login_complete_held = false;
        self.login_complete_sent = true;
        // Login complete carries sequence 0 and does not use the counter.
        self.send(build_login_complete(), Queue::Ui);
        if std::mem::take(&mut self.house_query_held) {
            self.outbox.send_action(build_house_query);
        }
        self.events.push_back(SessionEvent::LoginCompleteSent);
    }

    /// The character-selection state.
    #[must_use]
    pub fn selection(&self) -> &CharacterSelection {
        &self.selection
    }

    /// The character-creation state.
    #[must_use]
    pub fn creation(&self) -> &CharacterCreation {
        &self.creation
    }

    /// The selection state when it changed since the last call.
    pub fn take_selection_change(&mut self) -> Option<SelectionSnapshot> {
        let changed = !self.selection.drain_deltas().is_empty()
            && self.selection.revision() != self.selection_revision_seen;
        self.selection_revision_seen = self.selection.revision();
        changed.then(|| self.selection.snapshot())
    }

    /// The creation state when something a host shows changed since the
    /// last call (a finish sent or refused, an answer, a dismissal).
    pub fn take_creation_change(&mut self) -> Option<CreationSnapshot> {
        let shown = self.creation.drain_deltas().into_iter().any(|d| {
            !matches!(
                d.kind,
                CreationDeltaKind::Reset | CreationDeltaKind::StateChanged
            )
        });
        shown.then(|| self.creation.snapshot())
    }

    /// Take the bodies waiting to be sent, oldest first.
    pub fn drain_outgoing(&mut self) -> impl Iterator<Item = Outgoing> + '_ {
        self.outbox.drain()
    }

    /// The session's outgoing queue and game-action counter. Every owner
    /// that sends (chat, social, gameplay) queues through it, so action
    /// numbers come from one counter that log off resets.
    pub fn outbox_mut(&mut self) -> &mut Outbox {
        &mut self.outbox
    }

    /// Take the events that happened, oldest first.
    pub fn drain_events(&mut self) -> impl Iterator<Item = SessionEvent> + '_ {
        self.events.drain(..)
    }

    /// Handle one reassembled message body. Malformed messages are dropped,
    /// except in the data check, where a malformed answer fails the session.
    pub fn handle_message(&mut self, now: Duration, body: &[u8]) {
        match Inbound::parse(body) {
            Ok(message) => self.handle(now, &message),
            Err(_) => self.handle_unparsed(body),
        }
    }

    /// Handle one parsed message at session time `now`.
    pub fn handle(&mut self, now: Duration, message: &Inbound<'_>) {
        if self.phase == Phase::Failed {
            return;
        }
        self.handle_inbound(now, message);
        self.check_connected();
    }

    /// Handle a body that failed to parse: dropped, except in the data
    /// check, where a malformed answer fails the session.
    pub fn handle_unparsed(&mut self, body: &[u8]) {
        if self.phase == Phase::Failed {
            return;
        }
        match read_opcode(body).ok().and_then(Opcode::from_u32) {
            Some(Opcode::DddBegin) => {
                self.fail(SessionError::DataCheckFailed("malformed data-check start"));
            }
            Some(Opcode::DddEnd) => {
                self.fail(SessionError::DataCheckFailed(
                    "invalid data-check completion",
                ));
            }
            _ => {}
        }
        self.check_connected();
    }

    fn handle_inbound(&mut self, now: Duration, message: &Inbound<'_>) {
        match message {
            Inbound::CharacterList(list) => self.on_character_list(list),
            Inbound::ServerName(name) => {
                self.server_name = Some(name.clone());
                self.selection.apply_world_name(
                    &name.world_name,
                    u32::try_from(name.current_connections).ok(),
                );
                self.events
                    .push_back(SessionEvent::ServerName(name.clone()));
            }
            Inbound::CharacterError(error) => self.on_character_error(*error),
            Inbound::ServerReady => self.on_server_ready(),
            Inbound::CharacterDeleteAcknowledged => self.selection.apply_delete_acknowledged(),
            Inbound::CharGenVerification(response) => self.on_chargen_answer(now, response),
            Inbound::DddInterrogation => {
                self.data_interrogation_received = true;
                match build_interrogation_response(None) {
                    Ok(body) => self.send(body, Queue::Database),
                    Err(error) => self.fail(error.into()),
                }
            }
            Inbound::DddBegin(begin) => {
                if begin.requires_update() {
                    self.fail(SessionError::DataUpdateRequired);
                }
            }
            Inbound::DddDataMessage => self.fail(SessionError::DataUpdateRequired),
            Inbound::DddEnd => {
                // The echo goes back once, and only if the server asked for
                // our versions first.
                if !self.data_check_complete && self.data_interrogation_received {
                    self.send(build_data_check_complete(), Queue::Database);
                }
                self.data_check_complete = true;
            }
            Inbound::CreateObject(object) => self.on_create_object(object.guid),
            Inbound::LogOffConfirmed => {
                if self.phase == Phase::LoggingOff {
                    match self.logoff_intent {
                        LogoffIntent::Disconnect => self.finish_logoff(true),
                        LogoffIntent::ReturnToSelection => self.return_to_selection(now),
                    }
                }
            }
            // Single-quality and inventory updates belong to the world
            // model, not the login flow.
            Inbound::PublicUpdatePropertyInt(_)
            | Inbound::PrivateUpdatePropertyInt(_)
            | Inbound::PrivateUpdatePropertyInt64(_)
            | Inbound::PrivateUpdatePropertyBool(_)
            | Inbound::PublicUpdatePropertyBool(_)
            | Inbound::PublicUpdatePropertyString(_)
            | Inbound::PrivateUpdatePropertyString(_)
            | Inbound::PublicUpdatePropertyDataId(_)
            | Inbound::PrivateUpdatePropertyDataId(_)
            | Inbound::PublicUpdatePropertyInstanceId(_)
            | Inbound::PrivateUpdatePropertyInstanceId(_)
            | Inbound::PrivateUpdatePosition(_)
            | Inbound::PrivateUpdateVital(_)
            | Inbound::PrivateUpdateVitalCurrent(_)
            | Inbound::PrivateUpdateAttribute(_)
            | Inbound::PrivateUpdateSkill(_)
            | Inbound::SetStackSize(_)
            | Inbound::InventoryRemoveObject(_) => {}
            // World-object messages belong to the world model.
            Inbound::GameEvent(_)
            | Inbound::UpdateObject(_)
            | Inbound::DeleteObject(_)
            | Inbound::PickupEvent(_)
            | Inbound::ParentEvent(_)
            | Inbound::SetState(_)
            | Inbound::UpdatePosition(_)
            | Inbound::VectorUpdate(_)
            | Inbound::UpdateMotion(_)
            | Inbound::ObjDescEvent(_)
            | Inbound::PlayerTeleport(_)
            | Inbound::Other { .. } => {}
            // Gameplay messages are not routed by the login flow.
            Inbound::HearSpeech(_)
            | Inbound::EmoteText(_)
            | Inbound::SoulEmote(_)
            | Inbound::ServerMessage(_)
            | Inbound::TurbineChat(_)
            | Inbound::PlayerKilled(_)
            | Inbound::SoundEvent(_)
            | Inbound::PlayScript(_)
            | Inbound::PlayScriptType(_)
            | Inbound::AdminEnvirons(_) => {}
        }
    }

    fn on_character_list(&mut self, list: &CharacterList) {
        self.characters = Some(list.clone());
        self.creates_since_character_list = 0;
        if matches!(self.phase, Phase::Connecting | Phase::InCharacterSelect) {
            self.apply_roster();
        }
        self.events
            .push_back(SessionEvent::CharacterList(list.clone()));
    }

    fn apply_roster(&mut self) {
        let Some(list) = &self.characters else {
            return;
        };
        let roster: Vec<(u32, String, u32)> = list
            .characters
            .iter()
            .map(|c| (c.id, c.name.clone(), c.seconds_greyed_out))
            .collect();
        self.selection
            .apply_roster(&list.account_name, list.slot_count, &roster);
    }

    /// Choose a character and ask the server to enter the world with it.
    ///
    /// # Errors
    /// Outside character selection, or no available character matches.
    pub fn enter_world(
        &mut self,
        now: Duration,
        selector: &CharacterSelector,
    ) -> Result<(), SessionError> {
        self.require_selection("entering the world")?;
        let list = self.characters.as_ref().ok_or(SessionError::WrongPhase {
            request: "entering the world",
            phase: self.phase,
        })?;
        let id = select_character(list, selector)
            .map(|index| list.characters[index].id)
            .ok_or_else(|| SessionError::NoCharacter(format!("{selector:?}")))?;
        if !self.selection.try_highlight(id) {
            return Err(SessionError::NoCharacter(format!("{selector:?}")));
        }
        self.enter_highlighted(now)
    }

    /// Enter the world with the highlighted character.
    ///
    /// # Errors
    /// Outside character selection, or the highlighted character cannot
    /// enter (none, pending deletion, a delete open).
    pub fn enter_highlighted(&mut self, now: Duration) -> Result<(), SessionError> {
        self.require_selection("entering the world")?;
        let entry = self
            .selection
            .begin_enter()
            .ok_or_else(|| SessionError::NoCharacter("the highlighted character".to_owned()))?;
        let body = match build_enter_world(entry.character_id, self.selection.account_name()) {
            Ok(body) => body,
            Err(error) => {
                self.selection.return_to_selection();
                return Err(error.into());
            }
        };
        let character = Character {
            id: entry.character_id,
            name: entry.name,
            seconds_greyed_out: 0,
        };
        self.pending_enter_world = Some((character.clone(), body));
        self.active_character = Some(character);
        self.send(build_enter_world_request(), Queue::Ui);
        self.set_phase(Phase::EnteringWorld);
        self.deadline = Some(now + ENTER_WORLD_TIMEOUT);
        Ok(())
    }

    /// Highlight a character on the selection screen. `Index` is the
    /// server's list index; a name matches characters pending deletion too.
    ///
    /// # Errors
    /// Outside character selection, or no character matches.
    pub fn highlight(&mut self, selector: &CharacterSelector) -> Result<u32, SessionError> {
        self.require_selection("highlighting a character")?;
        let id = resolve_entry(self.selection.entries(), selector)
            .ok_or_else(|| SessionError::NoCharacter(format!("{selector:?}")))?;
        if self.selection.try_highlight(id) {
            Ok(id)
        } else {
            Err(SessionError::Refused(
                "the character cannot be highlighted now".to_owned(),
            ))
        }
    }

    /// Open the delete confirmation for the highlighted character.
    ///
    /// # Errors
    /// Outside character selection, or the character cannot be deleted.
    pub fn request_delete(&mut self) -> Result<u32, SessionError> {
        self.require_selection("deleting a character")?;
        self.selection.try_request_delete().ok_or_else(|| {
            SessionError::Refused("the highlighted character cannot be deleted".to_owned())
        })
    }

    /// Confirm the open delete: the request goes out.
    ///
    /// # Errors
    /// Outside character selection, or no delete confirmation is open.
    pub fn confirm_delete(&mut self) -> Result<u32, SessionError> {
        self.require_selection("deleting a character")?;
        let (entry, account) = self
            .selection
            .try_take_delete_confirmation()
            .ok_or_else(|| SessionError::Refused("no delete is waiting to be confirmed".into()))?;
        let slot = u32::try_from(entry.active_index).unwrap_or(u32::MAX);
        let body = build_character_delete(&account, slot)?;
        self.send(body, Queue::Login);
        Ok(entry.character_id)
    }

    /// Restore the highlighted character (pending deletion).
    ///
    /// # Errors
    /// Outside character selection, or the character is not pending
    /// deletion.
    pub fn restore(&mut self, now: Duration) -> Result<u32, SessionError> {
        self.require_selection("restoring a character")?;
        let entry = self.selection.try_begin_restore(now).ok_or_else(|| {
            SessionError::Refused("the highlighted character cannot be restored".to_owned())
        })?;
        self.pending_chargen = PendingChargen::Restore;
        self.send(build_character_restore(entry.character_id), Queue::Control);
        Ok(entry.character_id)
    }

    /// Dismiss the delete confirmation or the error shown.
    ///
    /// # Errors
    /// Outside character selection, or nothing to dismiss.
    pub fn cancel(&mut self) -> Result<(), SessionError> {
        self.require_selection("cancelling")?;
        if self.selection.cancel() {
            Ok(())
        } else {
            Err(SessionError::Refused("nothing to cancel".to_owned()))
        }
    }

    /// Remember a character to enter with automatically after the next
    /// return to character selection (`None` forgets it).
    ///
    /// # Errors
    /// No such character can enter the world.
    pub fn set_next_login(
        &mut self,
        selector: Option<&CharacterSelector>,
    ) -> Result<(), SessionError> {
        let Some(selector) = selector else {
            self.next_login_character_id = 0;
            return Ok(());
        };
        let id = resolve_entry(self.selection.entries(), selector)
            .filter(|id| {
                self.selection
                    .get(*id)
                    .is_some_and(SelectionEntry::can_enter)
            })
            .ok_or_else(|| SessionError::NoCharacter(format!("{selector:?}")))?;
        self.next_login_character_id = id;
        Ok(())
    }

    /// The character the next return to selection enters with (0: none).
    #[must_use]
    pub fn next_login_character_id(&self) -> u32 {
        self.next_login_character_id
    }

    /// A character-creation command. Finishing sends the create request.
    ///
    /// # Errors
    /// Outside character selection, or the creation state refused it.
    pub fn creation_command(&mut self, command: &CreationCommand) -> Result<(), SessionError> {
        self.require_selection("character creation")?;
        if let CreationCommand::Finish {
            confirm_unspent_credits,
        } = command
        {
            let roster = self.selection.entries().len();
            let slots = self.selection.snapshot().slot_count;
            let finish = self
                .creation
                .try_begin_finish(roster, slots, *confirm_unspent_credits)
                .map_err(|refusal| {
                    SessionError::Refused(format!("creation refused: {refusal:?}"))
                })?;
            let body = build_character_create(
                self.selection.account_name(),
                &finish.request,
                &finish.skill_advancement_classes,
            )?;
            self.pending_chargen = PendingChargen::Create;
            self.send(body, Queue::Login);
            return Ok(());
        }
        match self.creation.apply_command(command) {
            None | Some(true) => Ok(()),
            Some(false) => Err(SessionError::Refused(format!(
                "character creation refused {command:?}"
            ))),
        }
    }

    fn require_selection(&self, request: &'static str) -> Result<(), SessionError> {
        if self.phase == Phase::InCharacterSelect {
            Ok(())
        } else {
            Err(SessionError::WrongPhase {
                request,
                phase: self.phase,
            })
        }
    }

    /// Ask the server to take the character out of the world; the session
    /// ends when it confirms.
    ///
    /// # Errors
    /// Not in the world.
    pub fn request_logoff(&mut self, now: Duration) -> Result<(), SessionError> {
        self.begin_logoff(now, LogoffIntent::Disconnect)
    }

    /// Ask the server to take the character out of the world and return to
    /// character selection on the same connection.
    ///
    /// # Errors
    /// Not in the world.
    pub fn request_return_to_selection(&mut self, now: Duration) -> Result<(), SessionError> {
        self.begin_logoff(now, LogoffIntent::ReturnToSelection)
    }

    fn begin_logoff(&mut self, now: Duration, intent: LogoffIntent) -> Result<(), SessionError> {
        let character = match (&self.active_character, self.phase) {
            (Some(c), Phase::InWorld) if c.id != 0 => c.id,
            _ => {
                return Err(SessionError::WrongPhase {
                    request: "log off",
                    phase: self.phase,
                });
            }
        };
        self.logoff_intent = intent;
        self.send(build_logoff_request(character), Queue::Ui);
        self.set_phase(Phase::LoggingOff);
        self.deadline = Some(now + LOGOFF_CONFIRMATION_TIMEOUT);
        Ok(())
    }

    /// Advance timers.
    pub fn tick(&mut self, now: Duration) {
        self.selection.sweep_restore_correlation(now);
        let Some(deadline) = self.deadline else {
            return;
        };
        if now < deadline {
            return;
        }
        match self.phase {
            Phase::Connecting => {
                let step = if self.characters.is_none() {
                    "character list not received"
                } else {
                    "the server did not complete the game-data check"
                };
                self.fail(SessionError::ConnectTimeout(step));
            }
            Phase::EnteringWorld => self.fail(SessionError::EnterWorldTimeout),
            Phase::LoggingOff => self.finish_logoff(false),
            _ => self.deadline = None,
        }
    }

    /// Fail because the transport could not finish connecting (no answer to
    /// the login request).
    pub fn fail_connect(&mut self, step: &'static str) {
        self.fail(SessionError::ConnectTimeout(step));
    }

    /// The transport lost the connection (the server fell silent): the
    /// session fails in whatever phase it is in. A session that already
    /// ended keeps its end.
    pub fn lose_connection(&mut self) {
        if matches!(self.phase, Phase::LoggedOff | Phase::Failed) {
            return;
        }
        self.pending_enter_world = None;
        self.fail(SessionError::ConnectionLost);
    }

    fn on_character_error(&mut self, error: CharacterError) {
        if error.is_sentinel() {
            return;
        }
        self.selection.apply_error(error);
        // Another log in on the account, a server going down and the like
        // end the connection at once, in the world or not: the server
        // drops it right after, and waiting would only run out a timeout.
        if super::selection::ends_the_session(error)
            && !matches!(self.phase, Phase::LoggedOff | Phase::Failed)
        {
            self.pending_enter_world = None;
            self.fail(SessionError::ServerEnded {
                code: error.raw_error_code,
                reason: super::selection::error_message(error),
            });
            return;
        }
        if self.phase == Phase::EnteringWorld {
            self.pending_enter_world = None;
            self.active_character = None;
            self.deadline = None;
            self.events
                .push_back(SessionEvent::Failed(SessionError::CharacterRejected(
                    error.raw_error_code,
                )));
            self.set_phase(Phase::InCharacterSelect);
        }
    }

    fn on_chargen_answer(&mut self, now: Duration, response: &CharGenVerificationResponse) {
        // The same message answers a create and a restore; an answer nothing
        // asked for is dropped.
        match std::mem::replace(&mut self.pending_chargen, PendingChargen::None) {
            PendingChargen::None => {}
            PendingChargen::Restore => self.selection.apply_restore(response),
            PendingChargen::Create => self.on_creation_answer(now, response),
        }
    }

    fn on_creation_answer(&mut self, now: Duration, response: &CharGenVerificationResponse) {
        self.creation.apply_creation_response(response);
        let snapshot = self.creation.snapshot();
        if let Some(rejection) = snapshot.last_rejection {
            self.events.push_back(SessionEvent::CreationFailed {
                code: rejection.raw_code,
                reason: rejection.reason,
                name: rejection.attempted_name,
            });
            return;
        }
        let Some(created) = snapshot.last_created else {
            return;
        };
        self.events.push_back(SessionEvent::CharacterCreated {
            guid: created.guid,
            name: created.name.clone(),
        });
        // The new character joins the roster after the listed ones and the
        // session enters the world with it at once.
        let wire_index = self
            .characters
            .as_ref()
            .map_or(self.selection.entries().len(), |l| {
                l.characters.len() + self.creates_since_character_list
            });
        self.creates_since_character_list += 1;
        self.selection
            .append_created_character(created.guid, &created.name, wire_index);
        if self.selection.try_highlight(created.guid) {
            let _ = self.enter_highlighted(now);
        }
    }

    fn on_server_ready(&mut self) {
        if self.phase != Phase::EnteringWorld {
            return;
        }
        if let Some((character, body)) = self.pending_enter_world.take() {
            self.send(body, Queue::Ui);
            self.deadline = None;
            self.selection.complete_enter(character.id);
            self.creation.complete_enter();
            self.events.push_back(SessionEvent::EnteredWorld(character));
            self.set_phase(Phase::InWorld);
        }
    }

    fn on_create_object(&mut self, guid: u32) {
        let is_player = self.active_character.as_ref().is_some_and(|c| c.id == guid);
        if self.phase == Phase::InWorld
            && is_player
            && !self.login_complete_sent
            && !self.login_complete_held
        {
            if self.hold_login_complete {
                // The window presents the login: the house query goes now,
                // login complete at the end of its presentation.
                self.login_complete_held = true;
                self.outbox.send_action(build_house_query);
                return;
            }
            if self.login_awaits_entry {
                // No window: login complete and the house query go when the
                // character has entered the world.
                self.login_complete_held = true;
                self.house_query_held = true;
                return;
            }
            self.login_complete_sent = true;
            // Login complete carries sequence 0 and does not use the counter.
            self.send(build_login_complete(), Queue::Ui);
            self.outbox.send_action(build_house_query);
            self.events.push_back(SessionEvent::LoginCompleteSent);
        }
    }

    fn check_connected(&mut self) {
        if self.phase == Phase::Connecting && self.characters.is_some() && self.data_check_complete
        {
            self.deadline = None;
            self.set_phase(Phase::InCharacterSelect);
        }
    }

    fn finish_logoff(&mut self, confirmed: bool) {
        self.deadline = None;
        self.login_complete_sent = false;
        self.login_complete_held = false;
        self.house_query_held = false;
        self.outbox.reset_sequence();
        self.events.push_back(SessionEvent::LoggedOff { confirmed });
        self.set_phase(Phase::LoggedOff);
    }

    /// The server confirmed a log off back to selection: a new generation
    /// starts on the same connection with fresh selection and creation
    /// state, and the remembered next-login character enters at once.
    fn return_to_selection(&mut self, now: Duration) {
        self.deadline = None;
        self.login_complete_sent = false;
        self.login_complete_held = false;
        self.house_query_held = false;
        self.outbox.reset_sequence();
        self.events
            .push_back(SessionEvent::LoggedOff { confirmed: true });
        self.generation = self.generation.next();
        self.active_character = None;
        self.pending_enter_world = None;
        self.pending_chargen = PendingChargen::None;
        self.creates_since_character_list = 0;
        self.logoff_intent = LogoffIntent::Disconnect;
        self.selection.reset(self.generation);
        self.creation.reset(self.generation);
        self.selection.begin(self.generation);
        self.creation.begin(self.generation);
        self.set_phase(Phase::InCharacterSelect);
        self.apply_roster();
        if let Some(name) = &self.server_name {
            let population = u32::try_from(name.current_connections).ok();
            let world = name.world_name.clone();
            self.selection.apply_world_name(&world, population);
        }
        self.events
            .push_back(SessionEvent::ReturnedToCharacterSelect(self.generation));
        let next = self.next_login_character_id;
        if next != 0 && self.selection.try_highlight(next) && self.enter_highlighted(now).is_ok() {
            self.next_login_character_id = 0;
        }
    }

    fn fail(&mut self, error: SessionError) {
        if self.phase == Phase::Failed {
            return;
        }
        self.deadline = None;
        self.events.push_back(SessionEvent::Failed(error));
        self.set_phase(Phase::Failed);
    }

    fn send(&mut self, body: Vec<u8>, queue: Queue) {
        self.outbox.send(body, queue);
    }

    fn set_phase(&mut self, phase: Phase) {
        if self.phase != phase {
            self.phase = phase;
            self.events.push_back(SessionEvent::PhaseChanged(phase));
        }
    }
}

/// Index in the active list of the first available character the selector
/// matches.
fn select_character(list: &CharacterList, selector: &CharacterSelector) -> Option<usize> {
    list.characters
        .iter()
        .enumerate()
        .filter(|(_, c)| c.is_available_active_identity())
        .find(|(index, c)| match selector {
            CharacterSelector::FirstAvailable => true,
            CharacterSelector::Index(i) => i == index,
            CharacterSelector::Id(id) => *id == c.id,
            CharacterSelector::Name(name) => name.eq_ignore_ascii_case(&c.name),
        })
        .map(|(index, _)| index)
}

/// The id of the roster entry a selector names (pending deletion
/// included; `FirstAvailable` takes the first that can enter).
fn resolve_entry(entries: &[SelectionEntry], selector: &CharacterSelector) -> Option<u32> {
    entries
        .iter()
        .find(|e| match selector {
            CharacterSelector::FirstAvailable => e.can_enter(),
            CharacterSelector::Index(i) => e.active_index == *i,
            CharacterSelector::Id(id) => e.character_id == *id,
            CharacterSelector::Name(name) => name.eq_ignore_ascii_case(&e.name),
        })
        .map(|e| e.character_id)
}

#[cfg(test)]
#[path = "flow_selection_tests.rs"]
mod selection_tests;
#[cfg(test)]
#[path = "flow_tests.rs"]
mod tests;
