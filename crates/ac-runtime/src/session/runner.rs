//! Runs one session on its own thread: the UDP driver below, the session
//! flow above, commands in and events out over channels.
//!
//! The thread is the session's one update thread; everything the session
//! owns is touched only here. A session lives through generations: each
//! connection is one, and so is each return to character selection. A
//! reconnect logs the character off, closes the connection, waits for the
//! server to release the account and connects again, all on this thread.
//! A server that falls silent while no window is attached is logged in to
//! again the same way, on an interval, until the session is back.

use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender, TryRecvError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use ac_net::{
    Connection, DriverCounters, DriverError, DriverOptions, GameMessageGroup, LossCounters,
    LossOptions, TransportStats, UdpDriver,
};

use std::collections::{BTreeMap, VecDeque};

use ac_core::journal::JournalPage;
use ac_plugin::PluginSession;
use ac_plugin::abi::CharacterInfo;
use ac_plugin::abi::callback::InterceptDecision;
use ac_proto::{CharacterError, Inbound, Opcode, read_opcode};

use super::creation::{CreationCommand, CreationSnapshot, SeededRandom};
use super::diagnostics::{
    INBOUND_BUDGET, NetDiagnostics, NetPass, NetTickProbe, OpcodeDump, appearance_lines,
    appearance_parse_failed, format_net_final_line, inbound_budget_exceeded, received_motion_line,
};
use super::generation::{Generation, Lifecycle, LifecycleState};
use super::interface::{InterfaceEvent, InterfaceFeed};
use super::pregame::{
    ConnectionStatus, ConnectionView, PreGameEvent, PreGamePresenter, PreGameSource,
    connection_status,
};
use super::relogin::{
    AfterLoss, LOST_CONNECTION_RETRY_INTERVAL, Recovery, Relogin, after_loss, retries_after,
};
use super::selection::SelectionSnapshot;
use super::state::{Clocks, SessionState};
use super::{CharacterSelector, Phase, Queue, SessionError, SessionEvent, SessionFlow};
use crate::communication::{
    ChatChannelKind, ChatLine, ClientConfirmation, CommunicationPaths, ContractSummary,
    JournalCommand,
};
use crate::gameplay::{CorpseSummary, GameTables, GameplayCommand, GameplayEvent};
use crate::inventory::{AppraisalView, InventoryView, ItemCommand, TradeView, VendorView};
use crate::plugins::{LineOrigin, PluginBinding, PluginEvent, PluginLine};
use crate::world::transit::{LogoutStage, PortalSnapshot};
use crate::world::{ObjectDelta, PlayerView, WorldSnapshot};

/// How long one pass of the loop waits for datagrams. Short enough that
/// acks, NAKs and resends go out on time.
const PUMP_WAIT: Duration = Duration::from_millis(10);

/// How long the server has to answer the login request.
const LOGIN_TIMEOUT: Duration = Duration::from_secs(10);

/// How long a reconnect waits between closing the old connection and
/// opening the new one: the server releases an account about two seconds
/// after its character logs off.
pub const RECONNECT_QUIESCENCE: Duration = Duration::from_millis(2500);

/// What a session needs to start.
#[derive(Debug, Clone)]
pub struct SessionConfig {
    /// Login server address.
    pub server: SocketAddr,
    /// Account name.
    pub account: String,
    /// Account password.
    pub password: String,
    /// Which character to enter the world with.
    pub character: CharacterSelector,
    /// Simulated packet loss (0 % = off).
    pub loss: LossOptions,
    /// False for a probe: connect, read the character list, end without
    /// entering the world.
    pub enter_world: bool,
    /// Wait at character selection for the player to choose (the session
    /// was started without a character, from a window), instead of
    /// entering [`Self::character`] by itself.
    pub await_character_selection: bool,
    /// The host's data directory: `@log` files go to `logs/` and journals
    /// to `journal/` under it. `None` keeps the journal in memory and turns
    /// `@log` off.
    pub data_directory: Option<PathBuf>,
    /// The game tables read from the dats, shared by every session of the
    /// process. `None`: spells have no names or formulas, skill formula
    /// terms read 0 and character creation has nothing to choose from.
    pub tables: Option<Arc<GameTables>>,
    /// The dats the session loads collision and motion from, shared by
    /// every session of the process. `None`: the character does not move
    /// (no physics).
    pub content: Option<Arc<crate::movement::WorldContent>>,
    /// The network diagnostics to print (all off by default).
    pub diagnostics: NetDiagnostics,
    /// What the session document asks for after entering the world (none
    /// by default).
    pub login: LoginAutomation,
    /// The session's plugins (`None`: the session runs none and `/plugin`
    /// is not answered).
    pub plugins: Option<crate::plugins::PluginSetup>,
}

/// What a session document asks for once the character is in the world:
/// character options to put on it and commands to type.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct LoginAutomation {
    /// Character options by id (see [`crate::character::option_id`]) and the
    /// value each must have; set once login complete is out, only where the
    /// character differs.
    pub declared_options: Vec<(u32, bool)>,
    /// Lines typed into the chat box one at a time after entering the
    /// world.
    pub commands: Vec<String>,
    /// The time between two login commands.
    pub command_delay: Duration,
}

impl SessionConfig {
    /// Where the session's chat files go.
    #[must_use]
    pub fn communication_paths(&self) -> CommunicationPaths {
        CommunicationPaths {
            chat_log_directory: self.data_directory.as_ref().map(|d| d.join("logs")),
            journal_directory: self.data_directory.as_ref().map(|d| d.join("journal")),
        }
    }
}

/// A recall the player can ask for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Recall {
    /// To the lifestone.
    Lifestone,
    /// To the marketplace.
    Marketplace,
    /// To the player's house.
    House,
    /// To the allegiance's mansion.
    Mansion,
    /// To the allegiance hometown.
    AllegianceHometown,
}

/// Character selection and creation, and the session's own lifecycle.
#[derive(Debug, Clone, PartialEq)]
pub enum LifecycleCommand {
    /// Log the character off, close the connection, wait for the server to
    /// release the account, connect again and enter the world as at start.
    Reconnect,
    /// Log the character off back to character selection (the connection
    /// stays open; a new generation starts).
    LogOff,
    /// The player's own request to end the character session (the log out
    /// key, the indicator bar's button, Exit to Character Selection), with
    /// the game's gate: on the ground it is [`Self::LogOff`]; in the air it
    /// is refused with the game's line; without a moving character it does
    /// nothing.
    EndCharacterSession,
    /// Reply with [`HostEvent::CharacterSelection`].
    ShowSelection,
    /// Highlight a character on the selection screen.
    Highlight(CharacterSelector),
    /// Enter the world with this character (or the highlighted one).
    Enter(Option<CharacterSelector>),
    /// Open the delete confirmation for this character (or the highlighted
    /// one).
    RequestDelete(Option<CharacterSelector>),
    /// Confirm the open delete.
    ConfirmDelete,
    /// Restore this character (or the highlighted one) from pending
    /// deletion.
    Restore(Option<CharacterSelector>),
    /// Dismiss the delete confirmation or the error shown.
    Cancel,
    /// Enter with this character automatically after the next return to
    /// character selection (`None` forgets it).
    SetNextLogin(Option<CharacterSelector>),
    /// A character-creation request.
    Create(CreationCommand),
}

/// Requests from the host to a running session.
#[derive(Debug, Clone)]
pub enum SessionCommand {
    /// A line typed into the chat box, routed exactly as the chat box
    /// routes it: chat verbs, client commands, `@` server commands, or
    /// plain speech.
    Chat(String),
    /// A line typed into a chat window's entry, routed as the chat box
    /// routes it, with that window's talk focus: a line without a verb goes
    /// to `channel` (to `tell_target` when the focus is a tell).
    ChatInput {
        /// The line as typed.
        text: String,
        /// The talk-focus channel.
        channel: ChatChannelKind,
        /// The addressee of a plain line on the tell focus.
        tell_target: Option<String>,
        /// That addressee's object id, when picked in the world (0: by name).
        tell_target_guid: u32,
    },
    /// The one chat entry every front end types into: send a line with its
    /// talk focus (remembered for recall), aim it, stage or recall a draft.
    ChatEntry(crate::communication::ChatEntryCommand),
    /// Say this aloud.
    Say(String),
    /// Send a tell.
    Tell {
        /// The addressee's name.
        target: String,
        /// What to say.
        text: String,
    },
    /// Answer the last tell received.
    Reply(String),
    /// A text emote.
    Emote(String),
    /// Speak on a channel or chat room.
    Channel {
        /// Which one.
        channel: ChatChannelKind,
        /// What to say.
        text: String,
    },
    /// Recall somewhere.
    Recall(Recall),
    /// Add a friend by name.
    AddFriend(String),
    /// Remove a friend by name.
    RemoveFriend(String),
    /// Squelch (or with `add` false, unsquelch) a character, or with
    /// `account` their whole account.
    Squelch {
        /// Squelch or unsquelch.
        add: bool,
        /// The character's name.
        name: String,
        /// The whole account.
        account: bool,
    },
    /// Abandon a contract.
    AbandonContract(u32),
    /// Reply with [`HostEvent::Contracts`].
    ListContracts,
    /// Change the journal.
    Journal(JournalCommand),
    /// Reply with [`HostEvent::Journal`].
    ShowJournal,
    /// Change one character option (by id, see
    /// [`crate::character::option_id`]) the way the options panel does: saved
    /// at once or with the next block save, as the option says.
    SetCharacterOption {
        /// The option id.
        option: u32,
        /// On or off.
        value: bool,
    },
    /// Send the options block now if a change waits for it.
    SaveCharacterOptions,
    /// Reply with [`HostEvent::CharacterOptions`].
    ShowCharacterOptions,
    /// An item request (use, pick up, move, wield, appraise, buy, trade, ...).
    Item(ItemCommand),
    /// Reply with [`HostEvent::Inventory`].
    ListInventory,
    /// A selection, magic, combat, book, fellowship, allegiance or house
    /// request.
    Gameplay(GameplayCommand),
    /// Character selection, creation, log off, reconnect.
    Lifecycle(LifecycleCommand),
    /// Carry out `command` only in `generation`; in any other it is refused
    /// as stale (a request made before a reconnect never acts after it).
    AtGeneration {
        /// The generation the request was made in.
        generation: Generation,
        /// The request.
        command: Box<SessionCommand>,
    },
    /// Movement: held keys from a window, a scripted move, a jump, a
    /// posture, the run lock.
    Movement(crate::movement::MovementCommand),
    /// A window attaches: the session sends it a snapshot, then deltas,
    /// over this channel until [`SessionCommand::Detach`] or until the
    /// window drops its receiver.
    Attach(Sender<PresentationEvent>),
    /// The window detaches.
    Detach,
    /// Log the character off and end the session.
    Shutdown,
    /// Reply with [`HostEvent::Objects`]: every object the session knows.
    ListObjects,
    /// The player answered a question the client or the server asked
    /// ([`HostEvent::Confirmation`]).
    AnswerConfirmation {
        /// The question.
        confirmation: ClientConfirmation,
        /// Yes or no.
        accepted: bool,
    },
    /// The player answered whichever question is open (the newer of the
    /// client's and the server's); nothing when none is.
    AnswerOpenConfirmation {
        /// Yes or no.
        accepted: bool,
    },
    /// Reply with [`HostEvent::Corpses`]: the corpses within this many
    /// metres.
    ListCorpses(f32),
    /// Reply with [`HostEvent::GameTime`].
    GameTime,
    /// Spend experience or skill credits (the character window).
    Character(crate::character::CharacterCommand),
    /// Ping the server (the link-status panel); the answer's round trip
    /// shows in the next [`HostEvent::LinkStatus`].
    Ping,
    /// Reply with [`HostEvent::LinkStatus`].
    ShowLinkStatus,
    /// Let go of an object the server stopped talking about (by server
    /// id): it leaves as a server delete would take it; a refusal when the
    /// client does not know it or it is the character.
    DismissGhost(u32),
    /// Client-local status text a window raises (the portal-space wait
    /// line): it goes where the client's own interface text goes.
    ShowInterfaceText(String),
    /// Text a window's interface adds to the session's chat in a text
    /// class (OpenAC `RuntimeCommunicationState.AddText`): client-local
    /// text goes where the client's own interface text goes, any other
    /// class into the chat log, where it outlives the window and the
    /// plugins hear it.
    ShowText {
        /// The text.
        text: String,
        /// Its text class.
        log_text_type: ac_core::chat::LogTextType,
    },
    /// The attached window was minimized (`true`) or restored.
    WindowMinimized(bool),
    /// A map-coordinate link in the chat transcript was clicked (the
    /// plugins hear it).
    ChatLinkClicked {
        /// East (+) or west, map units.
        east_west: f64,
        /// North (+) or south, map units.
        north_south: f64,
        /// The text the link showed.
        text: String,
    },
    /// What the player did with the plugins' UI in the attached window (a
    /// control used, a plugin window opened or closed, canvas pointer input,
    /// the client windows it has).
    PluginUi(ac_plugin_ui::UiInput),
    /// The attached window has shown a portal destination the session holds
    /// the arrival for ([`crate::world::transit::PortalSpaceEvent::Destination`]):
    /// the arrival completes and the server hears login complete.
    DestinationShown(crate::world::transit::DestinationShown),
    /// The attached window has shown the login destination the session
    /// holds login complete for
    /// ([`crate::world::transit::PortalSpaceEvent::LoginDestination`]), with
    /// the readiness its reveal rule found (its generation and cell name the
    /// reveal): the login reveal completes and the server hears login
    /// complete.
    LoginShown(crate::world::transit::DestinationReadiness),
    /// Whether the attached window draws the world (it has the dats): such
    /// a window presents the login, and login complete waits for it
    /// ([`crate::world::transit::PortalSpaceEvent::LoginDestination`]).
    PresentsWorld(bool),
    /// The attached window has held the reveal `generation` for `waited`
    /// without its destination being ready (from five seconds on the reveal
    /// records that its wait cue showed).
    RevealWaited {
        /// The reveal.
        generation: u64,
        /// How long the window has held it.
        waited: Duration,
    },
}

/// What a session sends an attached window.
#[derive(Debug, Clone, PartialEq)]
pub enum PresentationEvent {
    /// Everything, at attach time.
    Snapshot(WorldSnapshot),
    /// Changes since the last message, one per object.
    Deltas(Vec<ObjectDelta>),
    /// The server's game clock: its tick count at the latest time sync. Sent
    /// after the snapshot and again at every sync; the window advances it
    /// by one tick per second in between.
    GameTime(f64),
    /// An object plays a sound (the server's sound event).
    Sound(ac_proto::effects::SoundEvent),
    /// An object plays a physics script by id (effects the server names
    /// directly).
    PlayScript(ac_proto::effects::PlayScript),
    /// An object plays the physics script of a type at an intensity
    /// (spell casts and hits, level-up, portalling in and out).
    PlayScriptType(ac_proto::effects::PlayScriptType),
    /// The server changed the environment (a fog override or a sound cue).
    AdminEnvirons(u32),
    /// Chat lines, oldest first: the whole transcript after the snapshot,
    /// then each new line. A line may come twice around the attach (same
    /// sequence number); the window keeps the first.
    Chat(Vec<ChatLine>),
    /// The player's vitals, selection and panel options: after the
    /// snapshot, then whenever they change.
    Player(PlayerView),
    /// What the item, character and magic panels read: the item table,
    /// the player's numbers and spellbook, the interaction facts and the
    /// appraisal answers; whole after the snapshot, then as they change.
    Interface(super::interface::InterfaceEvent),
    /// The player entered portal space or arrived at its destination
    /// (after the snapshot when the window attaches during one).
    PortalSpace(crate::world::transit::PortalSpaceEvent),
    /// What the pre-game screens show: the connection, character
    /// selection and creation, the creation choices (all after the
    /// snapshot, then as they change) and the refusals of the window's
    /// lifecycle requests.
    PreGame(super::pregame::PreGameEvent),
    /// Client-local status text for the status box: the lines on show
    /// after the snapshot (oldest first), then each new one.
    StatusText(String),
    /// A client command asked the window's interface for something
    /// (`/framerate`, `/saveui`, ...).
    WindowRequest(crate::communication::WindowRequest),
    /// The link, once a second.
    Link(super::LinkStatus),
    /// What the window draws of navigation (`/nav grid`, `/nav route` and
    /// walks under way), whenever it changes; empty when nothing is drawn.
    Navigation(crate::navigation::NavigationView),
    /// The motion states objects took up, in order, after the deltas of
    /// the same pass (the window takes each up, so a swing a later state
    /// replaced within the pass still plays).
    Motions(Vec<crate::world::AppliedMotion>),
    /// The instant the session clock counts from, after the snapshot: the
    /// window reads its frame's time on that clock to blend the local
    /// player between its physics steps
    /// ([`StepBlend`](crate::world::StepBlend)).
    SessionClock(Instant),
}

/// Messages from a running session to its host.
#[derive(Debug, Clone, PartialEq)]
pub enum HostEvent {
    /// Something happened in the session.
    Session(SessionEvent),
    /// The session's generation or lifecycle state changed.
    Lifecycle(Lifecycle),
    /// The character-selection screen changed (or was asked for).
    CharacterSelection(Box<SelectionSnapshot>),
    /// Character creation sent, was refused, or was answered (or was asked
    /// for).
    CharacterCreation(Box<CreationSnapshot>),
    /// Portal space changed (a reveal began, completed or was cancelled).
    Portal(PortalSnapshot),
    /// The log out moved to another stage.
    Logout(LogoutStage),
    /// Every object the session knows, nearest to the player first (the
    /// reply to [`SessionCommand::ListObjects`]).
    Objects(Vec<ObjectSummary>),
    /// The game date and time of day, once the server sent a time (the
    /// reply to [`SessionCommand::GameTime`]).
    GameTime(Option<ac_core::world::CalendarDate>),
    /// A chat line, worded as the chat box shows it.
    Chat(ChatLine),
    /// Client-local status text (a window shows it in its overlay).
    InterfaceText(String),
    /// A notice took the status path, in order with the chat lines, shown
    /// ([`Self::InterfaceText`] follows) or hidden by a plugin's chat
    /// filter: what the plugins hear as a status notice. A window has
    /// nothing to do with it.
    StatusNotice {
        /// The client's counter for status notices (never reset).
        sequence: u64,
        /// The text.
        text: String,
    },
    /// The quest tracker (the reply to [`SessionCommand::ListContracts`]).
    Contracts(Vec<ContractSummary>),
    /// The journal (the reply to [`SessionCommand::ShowJournal`]).
    Journal {
        /// Every page.
        pages: Vec<JournalPage>,
        /// The page being read, 1-based; 0 when there are none.
        current_page: usize,
    },
    /// The player's inventory (the reply to [`SessionCommand::ListInventory`]).
    Inventory(InventoryView),
    /// An appraisal answered the player's question.
    Appraisal(Box<AppraisalView>),
    /// The vendor's shop opened or refreshed (`Some`), or closed (`None`).
    Vendor(Option<Box<VendorView>>),
    /// The secure trade changed.
    Trade(TradeView),
    /// The server answered a mana query ([`ItemCommand::QueryMana`]).
    ItemMana(ac_core::items::ItemManaChange),
    /// A salvaging tool was used: the salvage panel opens on it.
    OpenSalvage(u32),
    /// Someone else's game board was used: its game began (the join went
    /// out unless the player already sits at a board) and the game window
    /// shows.
    BeginGame(u32),
    /// A pack the player carries was used: the inventory window shows and
    /// opens on it (the main pack when it is the player).
    OpenContainedContainer(u32),
    /// The link's numbers (the reply to [`SessionCommand::ShowLinkStatus`]).
    LinkStatus(super::LinkStatus),
    /// A login command could not be routed (the session goes on).
    LoginCommandFailed(crate::communication::LoginCommandFailure),
    /// The client asks the player a question (a yes/no dialog); answer with
    /// [`SessionCommand::AnswerConfirmation`].
    Confirmation(ClientConfirmation),
    /// The corpses nearby (the reply to [`SessionCommand::ListCorpses`]).
    Corpses(Vec<CorpseSummary>),
    /// Every character option by name (the reply to
    /// [`SessionCommand::ShowCharacterOptions`]).
    CharacterOptions(Vec<(&'static str, bool)>),
    /// A command could not be carried out (it named nothing known, the
    /// client's checks refused it, or it named an ended generation).
    CommandRefused(String),
    /// Something the gameplay owners show (selection, spells, combat, book,
    /// fellowship, allegiance, house).
    Gameplay(Box<GameplayEvent>),
    /// The movement owner reports (a move ended, a refusal, the status).
    Movement(crate::movement::MovementEvent),
    /// A walk's report changed, or the walk said something about its
    /// progress.
    Navigation(crate::navigation::NavigationEvent),
    /// A plugin logged a line, loaded, or failed.
    Plugin(crate::plugins::PluginEvent),
    /// The server fell silent with no window attached: the session logs in
    /// again, or an attempt to log in again failed.
    Relogin(Relogin),
    /// The session ended; nothing follows.
    Ended(SessionSummary),
}

/// One object, as a host shows it in a list.
#[derive(Debug, Clone, PartialEq)]
pub struct ObjectSummary {
    /// Server object id.
    pub guid: u32,
    /// Client-side id.
    pub local_id: u32,
    /// Name, if described.
    pub name: Option<String>,
    /// Full cell id, when placed in the world.
    pub cell_id: Option<u32>,
    /// Offset inside the landblock, meters.
    pub origin: Option<[f32; 3]>,
    /// Straight-line distance from the player, meters.
    pub distance: Option<f32>,
    /// The object holding this one, if any.
    pub parent: Option<u32>,
    /// Its allegiance's monarch, if it has one.
    pub monarch: Option<u32>,
}

/// How a session ended, with link counters for diagnostics.
#[derive(Debug, Clone, PartialEq)]
pub struct SessionSummary {
    /// Why it ended, if not cleanly.
    pub error: Option<String>,
    /// Whether the transport disconnect was sent.
    pub disconnect_sent: bool,
    /// Transport counters (resends, NAKs, checksum failures, ...) of the
    /// last connection.
    pub transport: Option<TransportStats>,
    /// Socket counters of the last connection.
    pub driver: Option<DriverCounters>,
    /// Simulated-loss counters, when loss was on.
    pub loss: Option<LossCounters>,
    /// Objects known when the last connection ended.
    pub world_objects: usize,
    /// Whether the player description arrived.
    pub player_described: bool,
    /// Messages that failed to parse, by opcode.
    pub parse_failures: BTreeMap<u32, u64>,
    /// Connections made (more than one after a reconnect).
    pub connections: u32,
    /// The server's own refusal, when a character error ended the session.
    pub server_refusal: Option<ServerRefusal>,
}

/// A character error the server ended the session with: its code, the
/// game's words for it, and whether it only says the login is still held.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerRefusal {
    /// The character error code as sent.
    pub code: u32,
    /// What the game says for it.
    pub message: String,
    /// The account or one of its characters is still logged on (see
    /// [`super::selection::means_still_logged_on`]).
    pub still_logged_on: bool,
}

impl ServerRefusal {
    /// The refusal behind `error`, when a character error from the server
    /// is what ended the session.
    #[must_use]
    pub fn from_error(error: &SessionError) -> Option<Self> {
        let (code, message) = match error {
            SessionError::ServerEnded { code, reason } => (*code, reason.clone()),
            SessionError::CharacterRejected(code) => {
                let error = CharacterError {
                    raw_error_code: *code,
                };
                (*code, super::selection::error_message(error))
            }
            _ => return None,
        };
        Some(Self {
            code,
            message,
            still_logged_on: super::selection::means_still_logged_on(CharacterError {
                raw_error_code: code,
            }),
        })
    }
}

/// The host's side of a running session.
#[derive(Debug)]
pub struct SessionHandle {
    commands: Sender<SessionCommand>,
    events: Receiver<HostEvent>,
    thread: Option<JoinHandle<()>>,
    progress: Arc<AtomicU64>,
}

impl SessionHandle {
    /// A sender for commands; clone it to command from elsewhere (a Ctrl+C
    /// handler, a window).
    #[must_use]
    pub fn commands(&self) -> Sender<SessionCommand> {
        self.commands.clone()
    }

    /// Wait up to `timeout` for the next event. `None` when none came or
    /// the session thread is gone.
    pub fn next_event(&self, timeout: Duration) -> Option<HostEvent> {
        self.events.recv_timeout(timeout).ok()
    }

    /// Wait for the session thread to finish.
    pub fn join(mut self) {
        if let Some(thread) = self.thread.take() {
            // A panic on the session thread is a bug; surface it.
            if let Err(panic) = thread.join() {
                std::panic::resume_unwind(panic);
            }
        }
    }

    /// The session thread has stopped (normally or by a fault) and no event
    /// is waiting: [`SessionHandle::next_event`] will never return one again.
    #[must_use]
    pub fn is_finished(&self) -> bool {
        self.thread.as_ref().is_none_or(JoinHandle::is_finished)
    }

    /// How far the session thread has got: a count it raises on every pass
    /// of its loop while connected. A count that stops rising while the
    /// thread lives means the session is stuck (a launcher shows the client
    /// as not responding).
    #[must_use]
    pub fn progress(&self) -> u64 {
        self.progress.load(Ordering::Relaxed)
    }

    /// Wait for the session thread to finish and say whether it faulted
    /// (panicked), without passing the fault on: a host running several
    /// sessions quarantines the faulted one and keeps the others.
    pub fn join_quarantining(mut self) -> SessionEnd {
        match self.thread.take().map(JoinHandle::join) {
            Some(Err(_)) => SessionEnd::Faulted,
            _ => SessionEnd::Finished,
        }
    }
}

impl Drop for SessionHandle {
    /// A handle dropped while its session still runs (a host thread that
    /// panicked, a host that returned early) logs the session off and
    /// waits for it: the process must never end with the character still
    /// in the world on the server.
    fn drop(&mut self) {
        let Some(thread) = self.thread.take() else {
            return;
        };
        if !thread.is_finished() {
            let _ = self.commands.send(SessionCommand::Shutdown);
        }
        // A fault on the session thread cannot be passed on from here (this
        // may already run during a panic); it was reported where it
        // happened.
        let _ = thread.join();
    }
}

/// How a session thread ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionEnd {
    /// It returned.
    Finished,
    /// It panicked; whatever it was doing was abandoned.
    Faulted,
}

/// Start a session on its own thread.
pub fn spawn(config: SessionConfig) -> std::io::Result<SessionHandle> {
    spawn_timed(config, Timing::GAME)
}

/// The waits a session keeps: the game's, or a test's shortened ones.
#[derive(Debug, Clone, Copy)]
struct Timing {
    /// How long the server may stay silent before the connection is lost.
    server_silence: Duration,
    /// The wait before each attempt to log in again after a loss.
    relogin_interval: Duration,
}

impl Timing {
    const GAME: Self = Self {
        server_silence: ac_net::SERVER_SILENCE_TIMEOUT,
        relogin_interval: LOST_CONNECTION_RETRY_INTERVAL,
    };
}

fn spawn_timed(config: SessionConfig, timing: Timing) -> std::io::Result<SessionHandle> {
    let (command_tx, command_rx) = mpsc::channel();
    let (event_tx, event_rx) = mpsc::channel();
    let progress = Arc::new(AtomicU64::new(0));
    let counter = Arc::clone(&progress);
    let thread = thread::Builder::new()
        .name(format!("session-{}", config.account))
        .spawn(move || run(&config, timing, &command_rx, &event_tx, &counter))?;
    Ok(SessionHandle {
        commands: command_tx,
        events: event_rx,
        thread: Some(thread),
        progress,
    })
}

/// How one connection ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DriveEnd {
    /// The host asked the session to end.
    Shutdown,
    /// The host asked for a reconnect.
    Reconnect,
    /// The server fell silent while no window was attached: the session
    /// logs in again.
    Lost,
}

/// Tells the host when the lifecycle changes, once per change.
struct LifecycleReporter<'a> {
    events: &'a Sender<HostEvent>,
    last: Option<Lifecycle>,
}

impl LifecycleReporter<'_> {
    fn report(&mut self, lifecycle: Lifecycle) {
        if self.last != Some(lifecycle) {
            self.last = Some(lifecycle);
            let _ = self.events.send(HostEvent::Lifecycle(lifecycle));
        }
    }
}

fn run(
    config: &SessionConfig,
    timing: Timing,
    commands: &Receiver<SessionCommand>,
    events: &Sender<HostEvent>,
    progress: &AtomicU64,
) {
    let mut summary = SessionSummary {
        error: None,
        disconnect_sent: false,
        transport: None,
        driver: None,
        loss: None,
        world_objects: 0,
        player_described: false,
        parse_failures: BTreeMap::new(),
        connections: 0,
        server_refusal: None,
    };
    let mut state = SessionState::new(config.communication_paths());
    if let Some(tables) = &config.tables {
        state.install_tables(Arc::clone(tables), &config.account);
    }
    if let Some(content) = &config.content {
        state.install_content(Arc::clone(content));
    }
    state.install_login_automation(
        &config.login.declared_options,
        config.login.commands.clone(),
        config.login.command_delay,
    );
    if config.diagnostics.probe_external_container {
        state.inventory.enable_external_container_probe();
    }
    let chargen = config
        .tables
        .as_ref()
        .map(|t| Arc::clone(t.chargen()))
        .unwrap_or_default();
    let mut lifecycle = LifecycleReporter { events, last: None };
    // The session's plugins live as long as the session, across reconnects
    // and returns to character selection, on this thread.
    let mut plugins = config
        .plugins
        .as_ref()
        .map(|setup| PluginSession::new(Arc::clone(&setup.engine), setup.options.clone()));
    if let Some((setup, peers)) = config
        .plugins
        .as_ref()
        .and_then(|setup| setup.peer_directory.as_ref().map(|peers| (setup, peers)))
    {
        // The other clients on this computer are found through notes in
        // the peer folder.
        let seed = state.plugin_services.next_seed();
        state
            .plugin_services
            .peers
            .configure(peers, &setup.peer_tags, seed);
    }
    if let Some(plugins) = plugins.as_mut() {
        // The plugins' UI measures text and sizes client art from the dats,
        // with or without a window.
        state
            .plugin_ui
            .set_resources(config.content.as_ref().map(|content| {
                Arc::new(crate::plugins::ui::DatUiResources::new(Arc::clone(content)))
                    as Arc<dyn ac_plugin_ui::UiResources>
            }));
        // The image files plugin markup names are read from each plugin's
        // folder on a worker, with or without the dats.
        state.plugin_ui.set_file_images(Some(Arc::new(
            ac_plugin::file_image::PluginFileImageLoader::new(),
        )));
        // The player's hotkey overrides, before any plugin registers one.
        if let Some(path) = config
            .plugins
            .as_ref()
            .and_then(|p| p.hotkey_overrides.clone())
        {
            state
                .plugin_ui
                .set_hotkey_overrides(crate::plugins::hotkey_file::load(&path));
            state.plugin_services.hotkey_file = Some(path);
        }
        let mut binding = PluginBinding::offline(&mut state, events, now_unix_ms());
        plugins.start(&mut binding);
        after_plugin_calls(plugins, &mut state, events);
    }
    let mut generation = Generation::default();
    let mut recovery = Recovery::new(timing.relogin_interval);
    // A window that attached while the session waited to log in again; the
    // next connection takes it.
    let mut waiting_window: Option<Sender<PresentationEvent>> = None;
    let result = loop {
        // A connection starts a generation; its end starts another.
        generation = generation.next();
        lifecycle.report(Lifecycle {
            generation,
            state: LifecycleState::Starting,
            player_guid: 0,
            has_transport: true,
        });
        let mut driver = match UdpDriver::bind(&DriverOptions {
            server: config.server,
            bind: None,
            loss: config.loss,
        }) {
            Ok(driver) => driver,
            Err(error) if recovery.is_recovering() => {
                generation = generation.next();
                lifecycle.report(Lifecycle::stopped(generation));
                let error = error.to_string();
                if !retry_later(&mut recovery, error, commands, events, &mut waiting_window) {
                    break Ok(());
                }
                continue;
            }
            Err(error) => break Err(error.to_string()),
        };
        summary.connections += 1;
        let connect_started = Instant::now();
        // What a failed attempt to log in again is judged by.
        let mut ended_by: Option<SessionError> = None;
        let mut logged_in = false;
        let mut window_after: Option<Sender<PresentationEvent>> = None;
        // Logging in hears the host meanwhile: a window that attaches sees
        // the connecting screen, a shutdown (its Cancel) ends the attempt,
        // and other requests wait for the connection.
        let mut connecting = Connecting {
            window: waiting_window.take(),
            queued: Vec::new(),
            started: connect_started,
            direct_launch: direct_launch(config, false, false),
            chargen: Arc::clone(&chargen),
        };
        let attempt = connecting.connect(&mut driver, config, commands, LOGIN_TIMEOUT);
        let end = match attempt {
            Ok(ConnectAttempt::Shutdown) => {
                lifecycle.report(Lifecycle::stopped(generation.next()));
                break Ok(());
            }
            Ok(ConnectAttempt::Connected(mut connection)) => {
                connection.set_server_silence_timeout(timing.server_silence);
                state.generation = generation;
                let mut session = Drive {
                    progress,
                    config,
                    flow: SessionFlow::with_generation(
                        driver.now().as_duration(),
                        generation,
                        Arc::clone(&chargen),
                        Box::new(SeededRandom::from_clock()),
                    ),
                    state: &mut state,
                    events,
                    attached: None,
                    shutdown_requested: false,
                    reconnect_requested: false,
                    enter_attempted: false,
                    auto_entering: false,
                    chat_in_world: false,
                    last_server_time: None,
                    last_player_view: None,
                    last_navigation_view: crate::navigation::NavigationView::default(),
                    presented_player: None,
                    opcode_dump: OpcodeDump::default(),
                    net_probe: NetTickProbe::default(),
                    interface: InterfaceFeed::default(),
                    chargen: Arc::clone(&chargen),
                    connect_started,
                    clock_origin: driver.origin(),
                    pregame: PreGamePresenter::default(),
                    failure: None,
                    link: (Duration::ZERO, 0.0),
                    link_sent: None,
                    plugins: plugins.as_mut(),
                    plugin_clock: None,
                    stay_character: None,
                    plugin_feed_closed: false,
                    ended_by: None,
                    logged_in: false,
                };
                if let Some(window) = connecting.window.take() {
                    session.dispatch(
                        SessionCommand::Attach(window),
                        driver.now().as_duration(),
                        clocks(&driver),
                    );
                }
                // What the host asked while the session logged in, in order.
                for command in connecting.queued.drain(..) {
                    session.dispatch(command, driver.now().as_duration(), clocks(&driver));
                }
                let end = session.drive(&mut driver, &mut connection, commands, &mut lifecycle);
                // However it ended, a stay in the world ends before the
                // owners forget it.
                session.announce_leaving(driver.now().as_duration(), clocks(&driver));
                ended_by = session.ended_by.take();
                logged_in = session.logged_in;
                window_after = session.attached.take();
                generation = session.flow.generation();
                // Chat still held for the plugins' filters is decided before
                // the connection's state is forgotten.
                if let Some(plugins) = plugins.as_mut() {
                    let mut binding = PluginBinding::offline(&mut state, events, now_unix_ms());
                    crate::plugins::filter_held_chat(plugins, &mut binding);
                }
                record_world(&mut summary, &state);
                // However the session ended, the journal is saved and the
                // server-owned state forgotten.
                state.end_generation(now_unix_ms());
                // The clock survives a log off but not its connection: the
                // next connection measures session time afresh.
                state.world.forget_clock();
                // A round trip belongs to its connection.
                state.ping = super::PingClock::default();
                send_all(state.drain_host_events(), events);
                // Every negotiated session ends with a transport disconnect.
                connection.disconnect();
                summary.disconnect_sent = driver.flush(&mut connection).is_ok();
                if config.diagnostics.probe_net {
                    eprintln!("{}", format_net_final_line(&connection.snapshot()));
                }
                summary.transport = Some(connection.stats().clone());
                end
            }
            Err(DriverError::Timeout(step)) => {
                let error = SessionError::ConnectTimeout(step);
                let text = error.to_string();
                ended_by = Some(error);
                connecting.failed(&text);
                Err(text)
            }
            Err(error) => {
                let text = error.to_string();
                connecting.failed(&text);
                Err(text)
            }
        };
        // A window that attached while logging in and was never handed to
        // a connection waits for the next one.
        waiting_window = connecting.window.take();
        summary.driver = Some(driver.counters());
        summary.loss = driver.loss_counters();
        generation = generation.next();
        if logged_in {
            recovery.logged_in();
        }
        match end {
            Ok(DriveEnd::Reconnect) => {
                lifecycle.report(Lifecycle::stopped(generation));
                if !wait_quiescence(commands, events) {
                    break Ok(());
                }
            }
            Ok(DriveEnd::Shutdown) => {
                lifecycle.report(Lifecycle::stopped(generation));
                break Ok(());
            }
            Ok(DriveEnd::Lost) => {
                // The end is recorded like any other (the host hears why
                // before the lifecycle stops); the session logs in again one
                // interval from now.
                recovery.begin(Instant::now());
                let _ = events.send(HostEvent::Relogin(Relogin::Scheduled {
                    after: recovery.interval(),
                }));
                lifecycle.report(Lifecycle::stopped(generation));
                if !wait_relogin(&mut recovery, commands, events, &mut waiting_window) {
                    break Ok(());
                }
            }
            Err(error) if recovery.is_recovering() && retries_after(ended_by.as_ref()) => {
                lifecycle.report(Lifecycle::stopped(generation));
                if window_after.is_some() {
                    waiting_window = window_after;
                }
                if !retry_later(&mut recovery, error, commands, events, &mut waiting_window) {
                    break Ok(());
                }
            }
            Err(error) => {
                lifecycle.report(Lifecycle {
                    state: LifecycleState::Faulted,
                    ..Lifecycle::stopped(generation)
                });
                summary.server_refusal = ended_by.as_ref().and_then(ServerRefusal::from_error);
                break Err(error);
            }
        }
    };
    // The plugins stop, the last loaded first, before the session reports
    // its end.
    if let Some(plugins) = plugins.as_mut() {
        let mut binding = PluginBinding::offline(&mut state, events, now_unix_ms());
        plugins.shutdown(&mut binding);
        after_plugin_calls(plugins, &mut state, events);
        state.plugin_services.peers.withdraw();
        send_all(state.drain_host_events(), events);
    }
    summary.error = result.err();
    // The host may already be gone; nothing to do then.
    let _ = events.send(HostEvent::Ended(summary));
}

/// A typed line through the plugins' interceptors (OpenAC
/// `ChatCommandRouter.SubmitEntered`): passed, it is sent on past them; a
/// rewrite is sent again from the start (the client's commands first) and
/// may be intercepted again, up to the bound; suppressed, or rewritten to
/// nothing, it goes nowhere. The command that sends it on, if any.
fn intercepted(
    plugins: &mut PluginSession,
    binding: &mut PluginBinding<'_>,
    line: String,
    rewrites: u32,
    origin: LineOrigin,
) -> Option<SessionCommand> {
    let (text, rewrites, passed) = match plugins.intercept(&line, binding) {
        InterceptDecision::Pass => (line, rewrites, true),
        InterceptDecision::Rewrite(text) if !text.trim().is_empty() => {
            (text.trim().to_owned(), rewrites + 1, false)
        }
        InterceptDecision::Rewrite(_) | InterceptDecision::Suppress => return None,
    };
    binding.state.plugin_lines.resubmit(&text, rewrites, passed);
    Some(match origin {
        LineOrigin::Chat => SessionCommand::Chat(text),
        LineOrigin::Entry {
            channel,
            tell_target,
            tell_target_guid,
        } => SessionCommand::ChatInput {
            text,
            channel,
            tell_target,
            tell_target_guid,
        },
    })
}

/// After plugin calls: their loads and failures go to the host, and the
/// chat path learns which verbs they answer now.
fn after_plugin_calls(
    plugins: &mut PluginSession,
    state: &mut SessionState,
    events: &Sender<HostEvent>,
) {
    for report in plugins.take_reports() {
        let _ = events.send(HostEvent::Plugin(PluginEvent::Report(report)));
    }
    state.plugin_lines.set_verbs(plugins.claimed_verbs());
    state
        .communication
        .set_plugin_filtering(plugins.filters_chat());
    // A rebind is the player's override, kept for the next run.
    if state.plugin_ui.take_hotkey_overrides_changed()
        && let Some(path) = state.plugin_services.hotkey_file.clone()
        && let Err(error) =
            crate::plugins::hotkey_file::save(&path, state.plugin_ui.hotkeys().overrides())
    {
        let _ = events.send(HostEvent::Plugin(PluginEvent::Log {
            plugin: ac_plugin::session::MANAGER_ID.to_owned(),
            level: ac_plugin::abi::LogLevel::Warn,
            message: format!(
                "plugin hotkeys: {} could not be written: {error}",
                path.display()
            ),
        }));
    }
    state
        .plugin_lines
        .set_intercepting(plugins.intercepts_input());
}

/// The world's counters for the summary, before the world is forgotten.
fn record_world(summary: &mut SessionSummary, state: &SessionState) {
    summary.world_objects = state.world.object_count();
    summary.player_described = state.world.player().is_some();
    for (code, count) in state.world.parse_failures() {
        *summary.parse_failures.entry(*code).or_default() += count;
    }
    for (code, count) in state.communication.parse_failures() {
        *summary.parse_failures.entry(*code).or_default() += count;
    }
}

/// Wait for the server to release the account before reconnecting. A
/// shutdown (or the host going away) ends the wait and the session; other
/// commands are refused until the new connection is up. Returns whether to
/// reconnect.
fn wait_quiescence(commands: &Receiver<SessionCommand>, events: &Sender<HostEvent>) -> bool {
    let deadline = Instant::now() + RECONNECT_QUIESCENCE;
    loop {
        let left = deadline.saturating_duration_since(Instant::now());
        match commands.recv_timeout(left) {
            Ok(SessionCommand::Shutdown) | Err(RecvTimeoutError::Disconnected) => return false,
            Ok(_) => {
                let _ = events.send(HostEvent::CommandRefused(
                    "reconnecting: the request waits for the new connection".to_owned(),
                ));
            }
            Err(RecvTimeoutError::Timeout) => return true,
        }
    }
}

/// An attempt to log in again failed (`error`): the server is still gone
/// or not yet taking logins, so the next attempt waits another interval.
/// Returns whether to make it.
fn retry_later(
    recovery: &mut Recovery,
    error: String,
    commands: &Receiver<SessionCommand>,
    events: &Sender<HostEvent>,
    window: &mut Option<Sender<PresentationEvent>>,
) -> bool {
    recovery.attempt_failed(Instant::now());
    let _ = events.send(HostEvent::Relogin(Relogin::AttemptFailed {
        error,
        after: recovery.interval(),
    }));
    wait_relogin(recovery, commands, events, window)
}

/// Wait for the next attempt to log in again. A shutdown (or the host going
/// away) ends the wait and the session; a window attaching (or detaching
/// again) is kept for the next connection, which sends it the session as
/// it stands then; other commands are refused until the session is back.
/// Returns whether to log in again.
fn wait_relogin(
    recovery: &mut Recovery,
    commands: &Receiver<SessionCommand>,
    events: &Sender<HostEvent>,
    window: &mut Option<Sender<PresentationEvent>>,
) -> bool {
    loop {
        let now = Instant::now();
        let Some(deadline) = recovery.deadline() else {
            return true;
        };
        if recovery.take_due(now) {
            return true;
        }
        match commands.recv_timeout(deadline.saturating_duration_since(now)) {
            Ok(SessionCommand::Shutdown) | Err(RecvTimeoutError::Disconnected) => return false,
            Ok(SessionCommand::Attach(attached)) => *window = Some(attached),
            Ok(SessionCommand::Detach) => *window = None,
            Ok(_) => {
                let _ = events.send(HostEvent::CommandRefused(
                    "the connection to the server was lost: the request waits until the \
                     session has logged in again"
                        .to_owned(),
                ));
            }
            Err(RecvTimeoutError::Timeout) => {}
        }
    }
}

/// What one pass of inbound handling did.
#[derive(Debug, Clone, Copy)]
struct InboundPass {
    /// When it started (session time).
    started: Duration,
    /// Messages handled.
    processed: u64,
    /// It stopped at the budget with messages left.
    budget_broke: bool,
}

/// What a connection's character selection does by itself, before the
/// player acts.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AtSelection {
    /// Nothing (already tried, or the session is ending).
    Nothing,
    /// A probe: end once the character list is in.
    EndProbe,
    /// Enter the configured character.
    Enter,
    /// Wait for the player to choose (no character was given).
    Wait,
}

/// [`AtSelection`] for `config` at a selection where the configured
/// character was (`enter_attempted`) or was not yet tried.
fn at_selection(
    config: &SessionConfig,
    enter_attempted: bool,
    shutdown_requested: bool,
) -> AtSelection {
    if !config.enter_world {
        AtSelection::EndProbe
    } else if shutdown_requested || enter_attempted {
        AtSelection::Nothing
    } else if config.await_character_selection {
        AtSelection::Wait
    } else {
        AtSelection::Enter
    }
}

/// The session enters its configured character by itself (a direct
/// launch): it has one to enter, and it has not tried this connection's
/// selection yet or is entering it now.
fn direct_launch(config: &SessionConfig, enter_attempted: bool, auto_entering: bool) -> bool {
    config.enter_world && !config.await_character_selection && (!enter_attempted || auto_entering)
}

/// One connection's loop and the state it keeps between passes.
struct Drive<'a> {
    /// Raised on every pass of the loop (see [`SessionHandle::progress`]).
    progress: &'a AtomicU64,
    config: &'a SessionConfig,
    flow: SessionFlow,
    state: &'a mut SessionState,
    events: &'a Sender<HostEvent>,
    attached: Option<Sender<PresentationEvent>>,
    shutdown_requested: bool,
    reconnect_requested: bool,
    /// The configured character was tried at this connection's character
    /// selection.
    enter_attempted: bool,
    /// The configured character is entering; a refusal ends the session.
    auto_entering: bool,
    chat_in_world: bool,
    last_server_time: Option<f64>,
    /// The player's view as last sent to the attached window.
    last_player_view: Option<PlayerView>,
    /// The navigation view as last sent to the attached window.
    last_navigation_view: crate::navigation::NavigationView,
    /// The player id the attached window was last given (in its snapshot).
    presented_player: Option<u32>,
    /// Opcodes already reported as unhandled (`OPENAC_DUMP_OPCODES`).
    opcode_dump: OpcodeDump,
    /// The once-a-second link line (`OPENAC_PROBE_NET`).
    net_probe: NetTickProbe,
    /// What the attached window's panels were last told.
    interface: InterfaceFeed,
    /// The creation choices, for the window's creation screens.
    chargen: Arc<ac_core::chargen::ChargenOptions>,
    /// When this connection attempt began.
    connect_started: Instant,
    /// The instant the session clock (the driver's `now`) counts from.
    clock_origin: Instant,
    /// What the attached window's pre-game screens were last told.
    pregame: PreGamePresenter,
    /// Why the session failed, once it did.
    failure: Option<SessionError>,
    /// When the last valid packet arrived (session time) and the loss
    /// percentage, as of this pass (the link-status reply reads them).
    link: (Duration, f64),
    /// When the window was last told about the link.
    link_sent: Option<Duration>,
    /// The session's plugins.
    plugins: Option<&'a mut PluginSession>,
    /// When the plugins were last pumped (session time).
    plugin_clock: Option<Duration>,
    /// The character the plugins are in the world with, kept through
    /// their stay so the leaving can still name it.
    stay_character: Option<CharacterInfo>,
    /// The session is stopping: the plugins heard the logoff and hear
    /// nothing more from the server (OpenAC detaches the session's events
    /// as it stops).
    plugin_feed_closed: bool,
    /// The session error that ended this connection, when one did.
    ended_by: Option<SessionError>,
    /// The connection got where the session's start takes it: in the world
    /// (or, for a session started without a character, at character
    /// selection waiting for the player; for a probe, the character list).
    logged_in: bool,
}

impl Drive<'_> {
    /// The session loop, from an accepted connect request to the end.
    fn drive(
        &mut self,
        driver: &mut UdpDriver,
        connection: &mut Connection,
        commands: &Receiver<SessionCommand>,
        lifecycle: &mut LifecycleReporter<'_>,
    ) -> Result<DriveEnd, String> {
        let mut pending: VecDeque<ac_net::InboundMessage> = VecDeque::new();
        loop {
            self.progress.fetch_add(1, Ordering::Relaxed);
            pending.extend(
                driver
                    .pump(connection, PUMP_WAIT)
                    .map_err(|e| e.to_string())?,
            );
            // A server silent for too long is gone: the connection ends now,
            // with nothing to log off from (the transport disconnect still
            // goes out as it ends). Whether a window is attached is read at
            // this moment: without one the session logs in again, with one
            // it ends and the window shows the loss.
            if connection.is_lost() {
                let window_attached = self.attached.is_some();
                self.flow.lose_connection();
                if let Some(error) = self.forward() {
                    let next = after_loss(
                        self.config.enter_world,
                        window_attached,
                        self.shutdown_requested,
                    );
                    if next == AfterLoss::LogInAgain {
                        return Ok(DriveEnd::Lost);
                    }
                    return self.end_with(error);
                }
            }
            // The local player moves before the network pass (its movement
            // event and jump go out with this pass), as the retail frame does.
            {
                let clocks = clocks(driver);
                let in_world = self.flow.phase() == Phase::InWorld;
                self.state
                    .advance_movement(self.flow.outbox_mut(), in_world, clocks);
            }
            let inbound = self.handle_inbound(driver, &mut pending);
            if self.config.diagnostics.probe_net {
                let link = connection.snapshot();
                let state = format!("{:?}", self.flow.phase());
                let pass = NetPass {
                    started: inbound.started,
                    processed: inbound.processed,
                    budget_broke: inbound.budget_broke,
                    queue_depth: pending.len(),
                    sent_total: driver.counters().sent,
                    link: &link,
                };
                if let Some(line) = self.net_probe.pass(&pass, &state) {
                    eprintln!("{line}");
                }
            }
            let now = driver.now().as_duration();
            let clocks = clocks(driver);
            self.flow.tick(now);
            self.follow_generation(now, clocks);
            self.sync_time(connection, now);
            self.follow_player(clocks);
            self.link = (
                connection.last_packet_at().as_duration(),
                connection.packet_loss_percent(),
            );
            // The window's link indicator and panel, once a second.
            if self
                .link_sent
                .is_none_or(|t| now.saturating_sub(t) >= Duration::from_secs(1))
            {
                self.link_sent = Some(now);
                let (last_packet_at, loss) = self.link;
                let status = super::LinkStatus::connected(
                    last_packet_at,
                    now,
                    self.state.ping.round_trip_seconds(),
                    loss,
                );
                if let Some(window) = &self.attached
                    && window.send(PresentationEvent::Link(status)).is_err()
                {
                    self.attached = None;
                }
            }

            loop {
                let command = match commands.try_recv() {
                    Err(TryRecvError::Disconnected) => {
                        self.shutdown_requested = true;
                        break;
                    }
                    Err(TryRecvError::Empty) => break,
                    Ok(command) => command,
                };
                self.dispatch(command, now, clocks);
            }
            self.follow_window();
            self.follow_generation(now, clocks);
            let in_world = self.flow.phase() == Phase::InWorld;
            self.state.tick(self.flow.outbox_mut(), in_world, clocks);
            let listening = in_world && self.flow.login_complete_sent();
            // The login commands also wait for the plugins' first tick in
            // the world: a command a plugin answers
            // must not reach it before what it does on arriving.
            let plugins_arrived =
                self.plugins.is_none() || crate::plugins::events::ticked_in_stay(self.state);
            self.state.follow_login(
                self.flow.outbox_mut(),
                in_world,
                listening,
                plugins_arrived,
                clocks,
            );
            let host_events = self.state.drain_host_events();
            self.publish(host_events);
            self.follow_window();
            // The plugins: typed verbs, then queued events and the tick; what
            // they changed of their UI goes to the window.
            self.run_plugins(now, clocks);
            self.present_plugin_ui();
            for line in self.state.inventory.take_probe_lines() {
                eprintln!("{line}");
            }

            match self.flow.phase() {
                Phase::InCharacterSelect => {
                    match at_selection(self.config, self.enter_attempted, self.shutdown_requested) {
                        AtSelection::EndProbe => {
                            self.logged_in = true;
                            self.shutdown_requested = true;
                        }
                        AtSelection::Enter => {
                            self.enter_attempted = true;
                            if let Err(error) = self.flow.enter_world(now, &self.config.character) {
                                self.forward();
                                return self.end_with(error);
                            }
                            self.auto_entering = true;
                        }
                        AtSelection::Wait => {
                            self.logged_in = true;
                            self.enter_attempted = true;
                        }
                        AtSelection::Nothing => {}
                    }
                }
                Phase::InWorld if self.shutdown_requested => {
                    // The plugins hear the stop first, with the session
                    // whole, and nothing from the server after it (OpenAC
                    // `Stop` announces the leave, then tears the session
                    // down).
                    self.announce_leaving(now, clocks);
                    self.plugin_feed_closed = true;
                    // Unsaved options go out before the character leaves.
                    self.state.save_options(self.flow.outbox_mut());
                    self.flow.request_logoff(now).map_err(|e| e.to_string())?;
                }
                Phase::InWorld => {
                    self.auto_entering = false;
                    self.logged_in = true;
                }
                _ => {}
            }
            self.follow_player(clocks);
            self.present_identity();
            // The character's entry, after the window has the world it
            // enters (the identity snapshot above).
            let entering = match self.flow.phase() {
                Phase::EnteringWorld => true,
                Phase::InWorld => !self.flow.login_complete_sent(),
                _ => false,
            };
            self.state.follow_entry(entering);
            self.follow_window();

            for outgoing in self.flow.drain_outgoing().collect::<Vec<_>>() {
                let group = match outgoing.queue {
                    Queue::Ui => GameMessageGroup::UiQueue,
                    Queue::Database => GameMessageGroup::DatabaseQueue,
                    Queue::Control => GameMessageGroup::ControlQueue,
                    Queue::Login => GameMessageGroup::LoginQueue,
                };
                connection
                    .send_message(&outgoing.body, group)
                    .map_err(|e| e.to_string())?;
            }
            driver.flush(connection).map_err(|e| e.to_string())?;

            if let Some(error) = self.forward() {
                return self.end_with(error);
            }
            let now_lifecycle = self.lifecycle();
            // `/status` reads where the session is in its life.
            self.state.lifecycle_state = now_lifecycle.state;
            lifecycle.report(now_lifecycle);
            let end = if self.reconnect_requested {
                DriveEnd::Reconnect
            } else {
                DriveEnd::Shutdown
            };
            match self.flow.phase() {
                Phase::LoggedOff => return Ok(end),
                // Not in the world, so there is no character to log off.
                Phase::Connecting | Phase::InCharacterSelect | Phase::EnteringWorld
                    if self.shutdown_requested =>
                {
                    return Ok(end);
                }
                _ => {}
            }
        }
    }

    /// The inbound messages of this pass, in order. In the world, handling
    /// gets a time budget per pass; the rest waits for the next pass so
    /// acks and resends stay on time. While logging in, everything is
    /// handled at once.
    fn handle_inbound(
        &mut self,
        driver: &UdpDriver,
        pending: &mut VecDeque<ac_net::InboundMessage>,
    ) -> InboundPass {
        let clock = Instant::now();
        let mut pass = InboundPass {
            started: driver.now().as_duration(),
            processed: 0,
            budget_broke: false,
        };
        // An arrival accepted in this pass is held only for a window that
        // is attached now.
        self.follow_window();
        let mut effects: Vec<PresentationEvent> = Vec::new();
        while let Some(message) = pending.pop_front() {
            let now = driver.now().as_duration();
            match Inbound::parse(&message.body) {
                Ok(parsed) => {
                    self.dump(&parsed, message.body.len());
                    self.flow.handle(now, &parsed);
                    let clocks = clocks(driver);
                    self.follow_generation(now, clocks);
                    self.follow_player(clocks);
                    let active = self.flow.phase() == Phase::InWorld;
                    self.state
                        .apply(&parsed, self.flow.outbox_mut(), active, clocks);
                    self.follow_held_login();
                    let portal = self.state.take_portal_events();
                    if self.attached.is_some() {
                        effects.extend(presentation_effect(&parsed));
                        effects.extend(portal.into_iter().map(PresentationEvent::PortalSpace));
                    }
                }
                Err(_) => {
                    self.flow.handle_unparsed(&message.body);
                    if let Ok(opcode) = read_opcode(&message.body)
                        && Opcode::from_u32(opcode).is_some()
                    {
                        self.state.world.record_parse_failure(opcode);
                        if opcode == Opcode::ObjDescEvent as u32
                            && self.config.diagnostics.dump_appearance
                        {
                            eprintln!("{}", appearance_parse_failed(message.body.len()));
                        }
                    }
                }
            }
            pass.processed += 1;
            let in_world = self.flow.phase() == Phase::InWorld;
            if inbound_budget_exceeded(in_world, Duration::ZERO, clock.elapsed(), INBOUND_BUDGET) {
                pass.budget_broke = true;
                break;
            }
        }
        self.present_identity();
        let changes: Vec<_> = self.state.world.drain_changes().collect();
        if let Some(plugins) = self.plugins.as_deref_mut()
            && !changes.is_empty()
            && !self.plugin_feed_closed
        {
            for event in crate::plugins::events::objects_changed(self.state, &changes) {
                plugins.queue_event(event);
            }
        }
        if let Some(window) = &self.attached
            && !changes.is_empty()
        {
            let deltas = self.state.world.deltas(&changes);
            let items = self.interface.world_changed(self.state, &changes);
            let gone = (!deltas.is_empty()
                && window.send(PresentationEvent::Deltas(deltas)).is_err())
                || items
                    .is_some_and(|items| window.send(PresentationEvent::Interface(items)).is_err());
            if gone {
                // The window went away without detaching.
                self.attached = None;
            }
        }
        let motions: Vec<_> = self.state.world.drain_applied_motions().collect();
        if self.config.diagnostics.motion_log {
            let watched = self.motion_log_watched();
            for applied in motions.iter().filter(|m| watched.contains(&Some(m.guid))) {
                eprintln!(
                    "[motion] {} {} 0x{:08X} serial={} {} {}",
                    crate::world::motion_log_stamp(),
                    if applied.taken_up {
                        "applied"
                    } else {
                        "interrupted"
                    },
                    applied.guid,
                    applied.motion.serial,
                    if applied.motion.body.is_some() {
                        "session-body"
                    } else {
                        "server-only"
                    },
                    crate::world::motion_line(&applied.motion.state)
                );
            }
        }
        // The motions follow the deltas, so the objects they name are known.
        if let Some(window) = &self.attached
            && !motions.is_empty()
            && window.send(PresentationEvent::Motions(motions)).is_err()
        {
            self.attached = None;
        }
        // Effects follow the deltas, so the objects they name are known.
        if let Some(window) = &self.attached
            && effects
                .into_iter()
                .any(|effect| window.send(effect).is_err())
        {
            self.attached = None;
        }
        pass
    }

    /// The session holds a portal arrival only while a window is attached:
    /// one that went away (detached, or dropped its receiver) releases it.
    /// Portal space a command changed (the window's answer, the release)
    /// reaches the window still attached.
    fn follow_window(&mut self) {
        self.state
            .set_window_attached(self.attached.is_some(), self.flow.outbox_mut());
        // An attached window presents the login: login complete waits for
        // the end of its presentation (OpenAC's window sends it there).
        self.flow
            .set_hold_login_complete(self.attached.is_some() && self.state.window_holds_login());
        // Without that window, a session that simulates the world sends
        // login complete when the character has entered it (OpenAC's
        // no-window host sends it when its first entry completes).
        self.flow
            .set_login_awaits_entry(self.state.movement.has_content());
        self.follow_held_login();
        let portal = self.state.take_portal_events();
        if let Some(window) = &self.attached
            && portal
                .into_iter()
                .any(|event| window.send(PresentationEvent::PortalSpace(event)).is_err())
        {
            self.attached = None;
            self.state
                .set_window_attached(false, self.flow.outbox_mut());
            self.flow.set_hold_login_complete(false);
            self.follow_held_login();
        }
    }

    /// Login complete the flow holds: with a window presenting the login,
    /// the player's cell begins the login reveal the window presents and it
    /// goes out when the window has shown the destination; without one (no
    /// window, or the window went) it goes when the character has entered
    /// the world (at once when the session simulates no world).
    fn follow_held_login(&mut self) {
        if !self.flow.login_complete_held() {
            self.state.take_login_release();
            return;
        }
        let window = self.attached.is_some() && self.state.window_holds_login();
        let entered =
            !self.state.movement.has_content() || self.state.movement.controller().is_some();
        if self.state.take_login_release() {
            if window || entered {
                self.flow.send_held_login_complete();
            }
            // Released to the no-window path before the character entered:
            // it goes when the entry is done (below, in a later pass).
            return;
        }
        if self.state.is_login_held() {
            return;
        }
        let cell = self
            .state
            .world
            .player_guid()
            .and_then(|guid| self.state.world.object(guid))
            .and_then(|object| object.position())
            .map(|position| position.cell_id);
        let held = match cell {
            Some(cell) if window => self.state.hold_login(cell),
            _ => false,
        };
        if !held && (window || entered) {
            self.flow.send_held_login_complete();
        }
    }

    /// The attached window learns who the player is when the session does:
    /// entering the world, and leaving it (a new generation forgets the
    /// world), each send a fresh snapshot.
    fn present_identity(&mut self) {
        if let Some(window) = &self.attached
            && let Some(snapshot) = identity_refresh(&self.state.world, &mut self.presented_player)
            && window.send(PresentationEvent::Snapshot(snapshot)).is_err()
        {
            self.attached = None;
        }
    }

    /// The objects the motion log follows: the player and the selection.
    fn motion_log_watched(&self) -> [Option<u32>; 2] {
        [
            self.state.world.player_guid(),
            self.state.gameplay.selection().selected(),
        ]
    }

    /// The diagnostics a parsed message asks for, when they are on.
    fn dump(&mut self, parsed: &Inbound<'_>, body_len: usize) {
        let diagnostics = self.config.diagnostics;
        if diagnostics.motion_log
            && let Inbound::UpdateMotion(update) = parsed
            && self.motion_log_watched().contains(&Some(update.guid))
        {
            eprintln!(
                "{}",
                received_motion_line(&crate::world::motion_log_stamp(), update)
            );
        }
        match parsed {
            Inbound::Other { opcode, .. } if diagnostics.dump_opcodes => {
                if let Some(line) = self.opcode_dump.unhandled(*opcode, body_len) {
                    eprintln!("{line}");
                }
            }
            Inbound::ObjDescEvent(event) if diagnostics.dump_appearance => {
                for line in appearance_lines(event) {
                    eprintln!("{line}");
                }
            }
            _ => {}
        }
    }

    /// A return to character selection started a new generation: the log
    /// out completes and every owner forgets the character.
    fn follow_generation(&mut self, now: Duration, clocks: Clocks) {
        if self.flow.generation() == self.state_generation() {
            return;
        }
        // The server confirmed the log off: the plugins hear it before the
        // owners forget the character (OpenAC `CompleteCharacterLogOff`).
        self.announce_leaving(now, clocks);
        self.state.transit.acknowledge_logout_confirmed();
        self.state.transit.complete_logout();
        self.state.end_generation(clocks.wall_unix_ms);
        self.state.generation = self.flow.generation();
        self.chat_in_world = false;
        let host_events = self.state.drain_host_events();
        self.publish(host_events);
    }

    fn state_generation(&self) -> Generation {
        self.state.generation
    }

    /// The world follows the character entering, and chat learns who and
    /// where the player is once in the world.
    fn follow_player(&mut self, clocks: Clocks) {
        if let Some(character) = self.flow.active_character()
            && self.state.world.player_guid() != Some(character.id)
        {
            self.state.world.set_player(character.id);
        }
        if !self.chat_in_world
            && self.flow.phase() == Phase::InWorld
            && let Some(character) = self.flow.active_character()
        {
            self.chat_in_world = true;
            let server = self
                .flow
                .server_name()
                .map_or("", |s| s.world_name.as_str())
                .to_owned();
            self.state.communication.entered_world(
                character.id,
                &server,
                &character.name,
                clocks.wall_unix_ms,
            );
        }
    }

    fn sync_time(&mut self, connection: &Connection, now: Duration) {
        let server_time = connection.server_time();
        if server_time > 0.0 && self.last_server_time != Some(server_time) {
            self.last_server_time = Some(server_time);
            self.state.world.sync_time(server_time, now);
            if let Some(window) = &self.attached
                && window
                    .send(PresentationEvent::GameTime(server_time))
                    .is_err()
            {
                self.attached = None;
            }
        }
    }

    /// Carry out one host command.
    fn dispatch(&mut self, command: SessionCommand, now: Duration, clocks: Clocks) {
        match command {
            SessionCommand::Shutdown => self.shutdown_requested = true,
            SessionCommand::AtGeneration {
                generation,
                command,
            } => {
                let current = self.flow.generation();
                if generation == current {
                    self.dispatch(*command, now, clocks);
                } else {
                    self.refuse(format!(
                        "stale generation {generation} (the session is in generation {current})"
                    ));
                }
            }
            SessionCommand::Lifecycle(command) => self.lifecycle_command(command, now),
            command => {
                let active = self.flow.phase() == Phase::InWorld;
                match self
                    .state
                    .command(command, self.flow.outbox_mut(), active, clocks)
                {
                    // The session clock goes ahead of the snapshot, so the
                    // window blends the player between its physics steps at
                    // its own frame times from the snapshot's first frame (a
                    // window moving from another session never draws the new
                    // steps on the old clock).
                    Some(SessionCommand::Attach(window))
                        if window
                            .send(PresentationEvent::SessionClock(self.clock_origin))
                            .is_ok()
                            && window
                                .send(PresentationEvent::Snapshot(self.state.world.snapshot()))
                                .is_ok() =>
                    {
                        // The clock as of now, so the window's sky starts at
                        // the right time of day.
                        if let Some(ticks) = self.state.world.clock().now_ticks(now) {
                            let _ = window.send(PresentationEvent::GameTime(ticks));
                        }
                        // A fog override the server set before the attach
                        // is still in force.
                        if let Some(environ) = self.state.world.environ_override() {
                            let _ = window.send(PresentationEvent::AdminEnvirons(environ));
                        }
                        // The chat and the player's panels, rebuilt from
                        // scratch.
                        let view = self.state.player_view();
                        let _ = window.send(PresentationEvent::Chat(
                            self.state.communication.transcript_lines(),
                        ));
                        let _ = window.send(PresentationEvent::Player(view));
                        self.last_player_view = Some(view);
                        // A new window starts with nothing drawn of
                        // navigation; the next pass sends what there is.
                        self.last_navigation_view = crate::navigation::NavigationView::default();
                        for text in self.state.communication.status_lines().into_iter().rev() {
                            let _ = window.send(PresentationEvent::StatusText(text));
                        }
                        self.presented_player = self.state.world.player_guid();
                        let active = self.flow.phase() == Phase::InWorld;
                        for event in self.interface.snapshot(self.state, active) {
                            let _ = window.send(PresentationEvent::Interface(event));
                        }
                        // The plugins' UI as it stands replaces anything
                        // still queued for a window.
                        drop(self.state.plugin_ui.take_changes());
                        self.state.plugin_ui.set_window_attached(true);
                        let _ = window.send(PresentationEvent::Interface(
                            InterfaceEvent::PluginUi(self.state.plugin_ui.snapshot()),
                        ));
                        // Portal space as it stands now replaces anything
                        // still queued for a window.
                        drop(self.state.take_portal_events());
                        for portal in self.state.portal_space_now() {
                            let _ = window.send(PresentationEvent::PortalSpace(portal));
                        }
                        // The pre-game screens, rebuilt from scratch.
                        let status = connection_status(self.flow.phase(), self.failure.as_ref());
                        let error = self.failure.as_ref().map(ToString::to_string);
                        let source = PreGameSource {
                            status,
                            error: error.as_deref(),
                            started: self.connect_started,
                            direct_launch: self.direct_launch(),
                            generation: self.flow.generation(),
                            selection: self.flow.selection(),
                            creation: self.flow.creation(),
                        };
                        for event in self.pregame.snapshot(&source, Some(&self.chargen)) {
                            let _ = window.send(PresentationEvent::PreGame(event));
                        }
                        self.attached = Some(window);
                    }
                    Some(SessionCommand::Detach) => self.attached = None,
                    Some(SessionCommand::GameTime) => {
                        let date = self.state.world.clock().date(now).and_then(Result::ok);
                        let _ = self.events.send(HostEvent::GameTime(date));
                    }
                    Some(SessionCommand::ShowLinkStatus) => {
                        let (last_packet_at, loss) = self.link;
                        let status = super::LinkStatus::connected(
                            last_packet_at,
                            now,
                            self.state.ping.round_trip_seconds(),
                            loss,
                        );
                        let _ = self.events.send(HostEvent::LinkStatus(status));
                    }
                    // Answered by the state owners, or a failed attach.
                    _ => {}
                }
            }
        }
    }

    fn lifecycle_command(&mut self, command: LifecycleCommand, now: Duration) {
        let result = match command {
            LifecycleCommand::Reconnect => {
                self.reconnect_requested = true;
                self.shutdown_requested = true;
                Ok(())
            }
            LifecycleCommand::LogOff => self.log_off(now),
            // OpenAC `RetailUiRuntime.EndCharacterSessionWithRetailGates`.
            LifecycleCommand::EndCharacterSession => {
                match end_session_gate(self.state.player_grounded()) {
                    EndSessionGate::LogOff => self.log_off(now),
                    EndSessionGate::MidAir => {
                        self.state.communication.show_interface_text(
                            now_unix_ms(),
                            ac_core::chat::client_text::CANT_LOG_OFF_MID_AIR,
                        );
                        Ok(())
                    }
                    EndSessionGate::Nothing => Ok(()),
                }
            }
            LifecycleCommand::ShowSelection => {
                let snapshot = self.flow.selection().snapshot();
                let _ = self
                    .events
                    .send(HostEvent::CharacterSelection(Box::new(snapshot)));
                Ok(())
            }
            LifecycleCommand::Highlight(selector) => self.flow.highlight(&selector).map(drop),
            LifecycleCommand::Enter(selector) => self
                .highlight_optional(selector.as_ref())
                .and_then(|()| self.flow.enter_highlighted(now)),
            LifecycleCommand::RequestDelete(selector) => self
                .highlight_optional(selector.as_ref())
                .and_then(|()| self.flow.request_delete().map(drop)),
            LifecycleCommand::ConfirmDelete => self.flow.confirm_delete().map(drop),
            LifecycleCommand::Restore(selector) => self
                .highlight_optional(selector.as_ref())
                .and_then(|()| self.flow.restore(now).map(drop)),
            LifecycleCommand::Cancel => self.flow.cancel(),
            LifecycleCommand::SetNextLogin(selector) => self.flow.set_next_login(selector.as_ref()),
            LifecycleCommand::Create(CreationCommand::Show) => {
                let snapshot = self.flow.creation().snapshot();
                let _ = self
                    .events
                    .send(HostEvent::CharacterCreation(Box::new(snapshot)));
                Ok(())
            }
            LifecycleCommand::Create(command) => self.flow.creation_command(&command),
        };
        if let Err(error) = result {
            // The window's screens close what waited on the request.
            if let Some(window) = &self.attached
                && window
                    .send(PresentationEvent::PreGame(PreGameEvent::Refused(
                        error.to_string(),
                    )))
                    .is_err()
            {
                self.attached = None;
            }
            self.refuse(error.to_string());
        }
        self.present_pregame();
    }

    /// The session enters its configured character by itself: before its
    /// first try at this connection's selection, and until that character
    /// is in the world (a log off back to selection leaves the choice to
    /// the player).
    fn direct_launch(&self) -> bool {
        direct_launch(self.config, self.enter_attempted, self.auto_entering)
    }

    /// Tell the attached window what changed on its pre-game screens.
    fn present_pregame(&mut self) {
        let Some(window) = &self.attached else {
            return;
        };
        let status = connection_status(self.flow.phase(), self.failure.as_ref());
        let error = self.failure.as_ref().map(ToString::to_string);
        let source = PreGameSource {
            status,
            error: error.as_deref(),
            started: self.connect_started,
            direct_launch: self.direct_launch(),
            generation: self.flow.generation(),
            selection: self.flow.selection(),
            creation: self.flow.creation(),
        };
        if self
            .pregame
            .changes(&source)
            .into_iter()
            .any(|event| window.send(PresentationEvent::PreGame(event)).is_err())
        {
            self.attached = None;
        }
    }

    fn highlight_optional(
        &mut self,
        selector: Option<&CharacterSelector>,
    ) -> Result<(), SessionError> {
        match selector {
            Some(selector) => self.flow.highlight(selector).map(drop),
            None => Ok(()),
        }
    }

    /// Log the character off to character selection: refused during a
    /// teleport or another log out; unsaved options go out first.
    fn log_off(&mut self, now: Duration) -> Result<(), SessionError> {
        if self.flow.phase() != Phase::InWorld {
            return Err(SessionError::WrongPhase {
                request: "log off",
                phase: self.flow.phase(),
            });
        }
        let player_killer = self.state.is_player_killer();
        if self.state.is_login_presenting() {
            return Err(SessionError::Refused(
                "the character is still entering the world".to_owned(),
            ));
        }
        if !self.state.transit.try_begin_logout_request(player_killer) {
            return Err(SessionError::Refused(
                "a log out or a teleport is under way".to_owned(),
            ));
        }
        self.state.save_options(self.flow.outbox_mut());
        self.flow.request_return_to_selection(now).inspect_err(|_| {
            self.state.transit.cancel_logout_request();
        })
    }

    /// Hand the host these events, and an attached window this pass's chat
    /// lines and the player's view when it changed.
    fn publish(&mut self, host_events: Vec<HostEvent>) {
        if let Some(window) = &self.attached
            && !present_to_window(
                window,
                &host_events,
                self.state.player_view(),
                &mut self.last_player_view,
            )
        {
            self.attached = None;
        }
        let requests = self.state.communication.take_window_requests();
        if let Some(window) = &self.attached
            && requests
                .into_iter()
                .any(|r| window.send(PresentationEvent::WindowRequest(r)).is_err())
        {
            self.attached = None;
        }
        if let Some(window) = &self.attached {
            let active = self.flow.phase() == Phase::InWorld;
            let gone = self
                .interface
                .follow(self.state, active, &host_events)
                .into_iter()
                .any(|event| window.send(PresentationEvent::Interface(event)).is_err());
            if gone {
                self.attached = None;
            }
        }
        if let Some(window) = &self.attached {
            let view = self.state.navigation.view();
            if *view != self.last_navigation_view {
                self.last_navigation_view = view.clone();
                if window
                    .send(PresentationEvent::Navigation(view.clone()))
                    .is_err()
                {
                    self.attached = None;
                }
            }
        }
        // Plugins hear every chat line and status notice, in order, and
        // what else the host events carry (a death, the server's question,
        // a portal), with their next events.
        // A line they heard right after the call that wrote it is not
        // handed out again.
        if let Some(plugins) = self.plugins.as_deref_mut() {
            if !self.plugin_feed_closed {
                let watch = &self.state.plugin_services.chat;
                for event in crate::plugins::chat::published_chat(watch, &host_events) {
                    plugins.queue_event(event);
                }
                for event in crate::plugins::events::host_events(self.state, &host_events) {
                    plugins.queue_event(event);
                }
            }
            self.state.plugin_services.chat.published();
        }
        send_all(host_events, self.events);
    }

    /// What the plugins changed of their UI this pass goes to the attached
    /// window (dropped without one: a window gets the whole state when it
    /// attaches).
    fn present_plugin_ui(&mut self) {
        let changes = self.state.plugin_ui.take_changes();
        if changes.is_empty() {
            return;
        }
        if let Some(window) = &self.attached
            && window
                .send(PresentationEvent::Interface(InterfaceEvent::PluginUi(
                    changes,
                )))
                .is_err()
        {
            self.attached = None;
        }
    }

    /// Hands the plugins the verbs typed this pass, then their queued events
    /// (a window attaching or leaving, chat, the world's changes) and the
    /// ticks due. Lines they submit are carried out right after the call
    /// that submitted them (and what those write is heard before the next
    /// call); what only the runner can do (a log off, ending the session)
    /// is carried out after their calls returned.
    fn run_plugins(&mut self, now: Duration, clocks: Clocks) {
        let Some(plugins) = self.plugins.as_deref_mut() else {
            return;
        };
        let attached = self.attached.is_some();
        plugins.set_window_attached(attached);
        self.state.plugin_ui.set_window_attached(attached);
        // The file icons plugin windows draw: reads that finished are held,
        // and those not drawn for a while let go.
        self.state
            .plugin_ui
            .tick_file_icons(u64::try_from(now.as_millis()).unwrap_or(u64::MAX));
        let in_world = self.flow.phase() == Phase::InWorld;
        if !self.plugin_feed_closed {
            let listening = in_world && self.flow.login_complete_sent();
            for event in crate::plugins::events::follow(self.state, in_world, listening) {
                plugins.queue_event(event);
            }
            for (owner, event) in crate::plugins::navigation::follow_previews(self.state) {
                plugins.queue_event_for(&owner, event);
            }
        }
        let delta = self
            .plugin_clock
            .map_or(Duration::ZERO, |last| clocks.session.saturating_sub(last));
        self.plugin_clock = Some(clocks.session);
        let lines = self.state.plugin_lines.take();
        // What plugins read of the character follows their stay (from the
        // arrival they heard to the leaving), not the session's phase.
        let in_stay = crate::plugins::events::in_stay(self.state);
        if in_stay {
            if let Some(character) = stay_character(&self.flow) {
                self.stay_character = Some(character);
            }
        } else {
            self.stay_character = None;
        }
        let character = self.stay_character.clone();
        let mut facts = plugin_facts(&self.flow);
        facts.in_world = in_stay;
        let generation = self.flow.generation().0;
        let mut binding = PluginBinding {
            state: &mut *self.state,
            events: self.events,
            window_attached: attached,
            character,
            wall_unix_ms: clocks.wall_unix_ms,
            link: Some(crate::plugins::Link {
                outbox: self.flow.outbox_mut(),
                active: in_world,
                clocks,
            }),
            facts,
            deferred: Vec::new(),
        };
        // Chat held for the plugins' filters is kept or dropped first,
        // before any plugin hears this pass's events.
        crate::plugins::filter_held_chat(plugins, &mut binding);
        for line in lines {
            match line {
                PluginLine::Verb(line) => {
                    plugins.handle_line(&line, &mut binding);
                }
                PluginLine::Intercept {
                    line,
                    rewrites,
                    origin,
                } => {
                    // Sent on at once, so what it writes is heard after the
                    // interceptors' calls and before the next.
                    if let Some(send) = intercepted(plugins, &mut binding, line, rewrites, origin) {
                        binding.run_now(send);
                    }
                }
            }
        }
        let ticks_before = plugins.ticks_raised();
        plugins.pump(delta.as_secs_f64(), Some(generation), &mut binding);
        let ticked = plugins.ticks_raised() != ticks_before;
        let deferred = std::mem::take(&mut binding.deferred);
        let submits = plugins.take_submits();
        after_plugin_calls(plugins, self.state, self.events);
        // A pump hands out its events before its ticks: a tick raised in the
        // stay came after the arrival, and the login commands may follow.
        if ticked {
            crate::plugins::events::note_ticked(self.state);
        }
        // The other clients' broadcast lines whose stagger is up run as
        // typed lines (OpenAC `RunDuePeerCommands`).
        let peer_lines = crate::plugins::peer_link::tick(
            self.state,
            plugin_character(&self.flow).as_ref(),
            in_world,
            delta.as_secs_f64(),
            clocks.wall_unix_ms,
        );
        for line in submits {
            self.dispatch(SessionCommand::Chat(line), now, clocks);
        }
        for line in peer_lines {
            self.dispatch(SessionCommand::Chat(line), now, clocks);
        }
        for command in deferred {
            self.dispatch(command, now, clocks);
        }
    }

    /// The character's stay in the world ends (a stop, a confirmed log off,
    /// a lost or ended connection): what the session has to tell is
    /// published first, then the plugins hear the leaving now (pending
    /// activations interrupted, then `Logoff`), while everything of the
    /// stay is still there to read, before any owner forgets it (OpenAC
    /// `LiveSessionController.AnnounceLeavingWorld`). Once per stay.
    fn announce_leaving(&mut self, now: Duration, clocks: Clocks) {
        if self.plugins.is_none() || !crate::plugins::events::in_stay(self.state) {
            return;
        }
        let pending = self.state.drain_host_events();
        self.publish(pending);
        let Some(plugins) = self.plugins.as_deref_mut() else {
            return;
        };
        for event in crate::plugins::events::leave(self.state) {
            plugins.queue_event(event);
        }
        let attached = self.attached.is_some();
        let active = self.flow.phase() == Phase::InWorld;
        let mut facts = plugin_facts(&self.flow);
        facts.in_world = true;
        let mut binding = PluginBinding {
            state: &mut *self.state,
            events: self.events,
            window_attached: attached,
            character: self.stay_character.take(),
            wall_unix_ms: clocks.wall_unix_ms,
            link: Some(crate::plugins::Link {
                outbox: self.flow.outbox_mut(),
                active,
                clocks,
            }),
            facts,
            deferred: Vec::new(),
        };
        plugins.deliver(&mut binding);
        let deferred = std::mem::take(&mut binding.deferred);
        let submits = plugins.take_submits();
        after_plugin_calls(plugins, self.state, self.events);
        for line in submits {
            self.dispatch(SessionCommand::Chat(line), now, clocks);
        }
        for command in deferred {
            self.dispatch(command, now, clocks);
        }
    }

    /// The connection ends with `error`, kept for judging an attempt to log
    /// in again.
    fn end_with(&mut self, error: SessionError) -> Result<DriveEnd, String> {
        let text = error.to_string();
        self.ended_by = Some(error);
        Err(text)
    }

    fn refuse(&self, text: String) {
        let _ = self.events.send(HostEvent::CommandRefused(text));
    }

    fn lifecycle(&self) -> Lifecycle {
        let phase = self.flow.phase();
        let state = match phase {
            Phase::Connecting | Phase::InCharacterSelect | Phase::EnteringWorld => {
                LifecycleState::Starting
            }
            Phase::InWorld => LifecycleState::InWorld,
            Phase::LoggingOff | Phase::LoggedOff => LifecycleState::Stopping,
            Phase::Failed => LifecycleState::Faulted,
        };
        Lifecycle {
            generation: self.flow.generation(),
            state,
            player_guid: match phase {
                Phase::InWorld => self.flow.active_character().map_or(0, |c| c.id),
                _ => 0,
            },
            has_transport: true,
        }
    }

    /// Pass the flow's events to the host, then the selection and creation
    /// changes. Returns the error that ends the session: a failure, or the
    /// refusal of the configured character (there is no other to fall back
    /// to). A character the host chose at selection may be refused and the
    /// session goes on.
    fn forward(&mut self) -> Option<SessionError> {
        let mut fatal = None;
        let auto = self.auto_entering;
        for event in self.flow.drain_events() {
            if let SessionEvent::Failed(error) = &event {
                let retryable = matches!(error, SessionError::CharacterRejected(_)) && !auto;
                if !retryable {
                    fatal = Some(error.clone());
                    self.failure = Some(error.clone());
                }
            }
            // The host may have stopped listening; the session still ends cleanly.
            let _ = self.events.send(HostEvent::Session(event));
        }
        if let Some(selection) = self.flow.take_selection_change() {
            let _ = self
                .events
                .send(HostEvent::CharacterSelection(Box::new(selection)));
        }
        if let Some(creation) = self.flow.take_creation_change() {
            let _ = self
                .events
                .send(HostEvent::CharacterCreation(Box::new(creation)));
        }
        self.present_pregame();
        fatal
    }
}

/// The character in the world as plugins read it: only while in the world.
fn plugin_character(flow: &SessionFlow) -> Option<CharacterInfo> {
    if flow.phase() != Phase::InWorld {
        return None;
    }
    stay_character(flow)
}

/// The character the session is playing, whatever the phase (the plugins'
/// stay outlasts the phase while a log off waits for the server).
fn stay_character(flow: &SessionFlow) -> Option<CharacterInfo> {
    let character = flow.active_character()?;
    Some(CharacterInfo {
        id: character.id,
        name: character.name.clone(),
        world: flow
            .server_name()
            .map_or_else(String::new, |s| s.world_name.clone()),
        ..CharacterInfo::default()
    })
}

/// What the session flow knows that plugins read: the account, its
/// characters, the world's population, the next login.
fn plugin_facts(flow: &SessionFlow) -> crate::plugins::SessionFacts {
    let list = flow.characters();
    crate::plugins::SessionFacts {
        in_world: flow.phase() == Phase::InWorld,
        account: list.map_or_else(String::new, |l| l.account_name.clone()),
        server_population: flow.server_name().map_or(-1, |s| s.current_connections),
        roster: list.map_or_else(Vec::new, |l| {
            l.characters
                .iter()
                .enumerate()
                .filter(|(_, c)| c.id != 0)
                .map(|(index, c)| crate::plugins::RosterEntry {
                    id: c.id,
                    name: c.name.clone(),
                    index: i32::try_from(index).unwrap_or(i32::MAX),
                    pending_delete: c.seconds_greyed_out != 0,
                })
                .collect()
        }),
        next_login: flow.next_login_character_id(),
    }
}

/// Both clocks, now.
fn clocks(driver: &UdpDriver) -> Clocks {
    Clocks {
        wall_unix_ms: now_unix_ms(),
        session: driver.now().as_duration(),
    }
}

/// What an attached window must hear about a parsed message that changes no
/// world state: the server's sound event, its physics-script plays and its
/// environment change.
fn presentation_effect(parsed: &Inbound) -> Option<PresentationEvent> {
    match parsed {
        Inbound::SoundEvent(sound) => Some(PresentationEvent::Sound(*sound)),
        Inbound::PlayScript(play) => Some(PresentationEvent::PlayScript(*play)),
        Inbound::PlayScriptType(play) => Some(PresentationEvent::PlayScriptType(*play)),
        Inbound::AdminEnvirons(environ) => {
            Some(PresentationEvent::AdminEnvirons(environ.environ_type))
        }
        _ => None,
    }
}

/// The current wall-clock time, UTC milliseconds since the Unix epoch. Chat
/// lines carry it; a clock before 1970 reads as 0.
pub(super) fn now_unix_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|d| i64::try_from(d.as_millis()).ok())
        .unwrap_or(0)
}

/// How long logging in waits for the network before it hears the host
/// again.
const CONNECT_POLL: Duration = Duration::from_millis(50);

/// How an attempt to log in ended.
enum ConnectAttempt {
    /// The server answered: the connection is up.
    Connected(Box<Connection>),
    /// The host asked the session to end first (the connecting screen's
    /// Cancel).
    Shutdown,
}

/// One attempt to log in, while the host is heard: OpenAC begins the
/// connection (`WorldSession.BeginConnect`, phase Connecting) and polls it
/// from the session's loop, so the window shows the connecting screen and
/// its Cancel works before the server answers.
struct Connecting {
    /// The window attached meanwhile (or before).
    window: Option<Sender<PresentationEvent>>,
    /// Other requests, kept for the connection in their order.
    queued: Vec<SessionCommand>,
    /// When this attempt began.
    started: Instant,
    /// The session enters its configured character by itself.
    direct_launch: bool,
    /// The creation choices, for a window's pre-game screens.
    chargen: Arc<ac_core::chargen::ChargenOptions>,
}

impl Connecting {
    /// Log in: the login request goes out, then the network is polled in
    /// short waits, the host's requests heard between them, until the
    /// server answers, the host asks the session to end, or `timeout`.
    fn connect(
        &mut self,
        driver: &mut UdpDriver,
        config: &SessionConfig,
        commands: &Receiver<SessionCommand>,
        timeout: Duration,
    ) -> Result<ConnectAttempt, DriverError> {
        if let Some(window) = &self.window {
            self.present(window, ConnectionStatus::Connecting, None, true);
        }
        let mut handshake = driver.begin_connect(&config.account, &config.password)?;
        let deadline = Instant::now() + timeout;
        loop {
            loop {
                match commands.try_recv() {
                    Ok(SessionCommand::Shutdown) | Err(TryRecvError::Disconnected) => {
                        return Ok(ConnectAttempt::Shutdown);
                    }
                    Ok(SessionCommand::Attach(window)) => {
                        self.present(&window, ConnectionStatus::Connecting, None, true);
                        self.window = Some(window);
                    }
                    Ok(SessionCommand::Detach) => self.window = None,
                    Ok(command) => self.queued.push(command),
                    Err(TryRecvError::Empty) => break,
                }
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(DriverError::Timeout("the connect request"));
            }
            if let Some(connection) =
                driver.poll_connect(&mut handshake, remaining.min(CONNECT_POLL))?
            {
                return Ok(ConnectAttempt::Connected(Box::new(connection)));
            }
        }
    }

    /// The attempt failed (`error`): the attached window's connecting
    /// screen says so.
    fn failed(&self, error: &str) {
        if let Some(window) = &self.window {
            self.present(window, ConnectionStatus::Failed, Some(error), false);
        }
    }

    /// Tell `window` where logging in is (with the creation choices when
    /// it just attached, as the connection's own attach does).
    fn present(
        &self,
        window: &Sender<PresentationEvent>,
        status: ConnectionStatus,
        error: Option<&str>,
        attached: bool,
    ) {
        if attached {
            let _ = window.send(PresentationEvent::PreGame(PreGameEvent::Options(
                Arc::clone(&self.chargen),
            )));
        }
        let view = ConnectionView::advance(
            None,
            status,
            error.map(str::to_owned),
            self.started,
            self.direct_launch,
        );
        let _ = window.send(PresentationEvent::PreGame(PreGameEvent::Connection(view)));
    }
}

/// What the player's own request to end the character session does
/// (OpenAC `RetailUiRuntime.EndCharacterSessionWithRetailGates`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum EndSessionGate {
    /// On the ground: log off.
    LogOff,
    /// In the air: refused with the game's line.
    MidAir,
    /// No moving character: nothing.
    Nothing,
}

/// [`EndSessionGate`] for a character standing on the ground (`grounded`;
/// `None` without one).
fn end_session_gate(grounded: Option<bool>) -> EndSessionGate {
    match grounded {
        Some(true) => EndSessionGate::LogOff,
        Some(false) => EndSessionGate::MidAir,
        None => EndSessionGate::Nothing,
    }
}

/// Hand an attached window what it shows besides the world: this pass's
/// chat lines, and the player's view when it changed since the last one
/// sent. False when the window went away.
fn present_to_window(
    window: &Sender<PresentationEvent>,
    host_events: &[HostEvent],
    view: PlayerView,
    last_view: &mut Option<PlayerView>,
) -> bool {
    let lines: Vec<ChatLine> = host_events
        .iter()
        .filter_map(|event| match event {
            HostEvent::Chat(line) => Some(line.clone()),
            _ => None,
        })
        .collect();
    if !lines.is_empty() && window.send(PresentationEvent::Chat(lines)).is_err() {
        return false;
    }
    for event in host_events {
        if let HostEvent::InterfaceText(text) = event
            && window
                .send(PresentationEvent::StatusText(text.clone()))
                .is_err()
        {
            return false;
        }
    }
    if *last_view != Some(view) {
        *last_view = Some(view);
        return window.send(PresentationEvent::Player(view)).is_ok();
    }
    true
}

/// Pass events to the host; it may have stopped listening, and the session
/// goes on.
/// A fresh snapshot for the attached window when the session's player is
/// not the one the window was last given (`presented`), which it updates.
fn identity_refresh(
    world: &crate::world::World,
    presented: &mut Option<u32>,
) -> Option<WorldSnapshot> {
    let current = world.player_guid();
    if current == *presented {
        return None;
    }
    *presented = current;
    Some(world.snapshot())
}

fn send_all(host_events: Vec<HostEvent>, events: &Sender<HostEvent>) {
    for event in host_events {
        let _ = events.send(event);
    }
}

#[cfg(test)]
mod tests {
    //! Fault containment (OpenAC `HeadlessProcessSchedulerTests`
    //! quarantine): a faulted session is seen as finished and its fault is
    //! contained.
    use super::*;

    fn handle(body: impl FnOnce() + Send + 'static) -> SessionHandle {
        let (commands, _command_rx) = mpsc::channel();
        let (event_tx, events) = mpsc::channel::<HostEvent>();
        let thread = thread::spawn(move || {
            let _keep = event_tx;
            body();
        });
        SessionHandle {
            commands,
            events,
            thread: Some(thread),
            progress: Arc::default(),
        }
    }

    fn wait_finished(h: &SessionHandle) {
        let start = std::time::Instant::now();
        while !h.is_finished() {
            assert!(start.elapsed() < Duration::from_secs(5), "never finished");
            thread::sleep(Duration::from_millis(5));
        }
    }

    #[test]
    fn a_faulted_session_is_finished_and_its_fault_is_contained() {
        let h = handle(|| panic!("synthetic session fault"));
        wait_finished(&h);
        assert!(h.next_event(Duration::from_millis(1)).is_none());
        assert_eq!(h.join_quarantining(), SessionEnd::Faulted);
    }

    #[test]
    fn script_plays_reach_the_window() {
        let mut body = 0xF754u32.to_le_bytes().to_vec();
        for word in [0x5000_0001u32, 0x3300_0042] {
            body.extend_from_slice(&word.to_le_bytes());
        }
        let parsed = Inbound::parse(&body).unwrap();
        assert!(matches!(
            presentation_effect(&parsed),
            Some(PresentationEvent::PlayScript(p))
                if p.guid == 0x5000_0001 && p.script_id == 0x3300_0042
        ));
        let mut body = 0xF755u32.to_le_bytes().to_vec();
        for word in [0x5000_0001u32, 0x21, 0.5f32.to_bits()] {
            body.extend_from_slice(&word.to_le_bytes());
        }
        let parsed = Inbound::parse(&body).unwrap();
        assert!(matches!(
            presentation_effect(&parsed),
            Some(PresentationEvent::PlayScriptType(p))
                if p.guid == 0x5000_0001 && p.script_type == 0x21 && p.intensity == 0.5
        ));
    }

    #[test]
    fn sound_and_environment_messages_reach_the_window_and_nothing_else_does() {
        let mut sound = 0xF750u32.to_le_bytes().to_vec();
        for word in [0x5000_0001u32, 0x03, 0.5f32.to_bits()] {
            sound.extend_from_slice(&word.to_le_bytes());
        }
        let parsed = Inbound::parse(&sound).unwrap();
        assert!(matches!(
            presentation_effect(&parsed),
            Some(PresentationEvent::Sound(s)) if s.guid == 0x5000_0001 && s.sound_type == 3
        ));
        let mut environ = (Opcode::AdminEnvirons as u32).to_le_bytes().to_vec();
        environ.extend_from_slice(&0x65u32.to_le_bytes());
        let parsed = Inbound::parse(&environ).unwrap();
        assert_eq!(
            presentation_effect(&parsed),
            Some(PresentationEvent::AdminEnvirons(0x65))
        );
        let other = 0xF7B1u32.to_le_bytes();
        let parsed = Inbound::parse(&other).ok();
        assert!(parsed.is_none_or(|p| presentation_effect(&p).is_none()));
    }

    #[test]
    fn ending_the_character_session_waits_for_the_ground() {
        // OpenAC `RetailUiRuntime.EndCharacterSessionWithRetailGates`: on
        // the ground it ends the session, in the air it is refused with
        // `CantLogOffMidAir`, outside player mode it does nothing.
        assert_eq!(end_session_gate(Some(true)), EndSessionGate::LogOff);
        assert_eq!(end_session_gate(Some(false)), EndSessionGate::MidAir);
        assert_eq!(end_session_gate(None), EndSessionGate::Nothing);
    }

    #[test]
    fn a_window_hears_each_chat_line_and_the_player_view_when_it_changes() {
        let line = |sequence: u64| ChatLine {
            sequence,
            kind: crate::communication::ChatKind::LocalSpeech,
            log_text_type: 2,
            combat_kind: None,
            received_unix_ms: 0,
            text: format!("line {sequence}"),
            channel_name: String::new(),
            sender: String::new(),
            sender_guid: 0,
            words: String::new(),
            display_text: String::new(),
            channel_id: 0,
            spans: None,
        };
        let (window, rx) = mpsc::channel();
        let mut last = None;
        let view = PlayerView {
            selected: Some(7),
            ..PlayerView::default()
        };
        let events = [
            HostEvent::Chat(line(1)),
            HostEvent::InterfaceText("not chat".into()),
            HostEvent::Chat(line(2)),
        ];
        assert!(present_to_window(&window, &events, view, &mut last));
        assert!(present_to_window(&window, &[], view, &mut last));
        let sent: Vec<PresentationEvent> = rx.try_iter().collect();
        assert_eq!(
            sent,
            [
                PresentationEvent::Chat(vec![line(1), line(2)]),
                PresentationEvent::StatusText("not chat".into()),
                PresentationEvent::Player(view),
            ],
            "nothing new the second time"
        );
        let changed = PlayerView {
            selected: None,
            ..view
        };
        assert!(present_to_window(&window, &[], changed, &mut last));
        assert_eq!(
            rx.try_iter().collect::<Vec<_>>(),
            [PresentationEvent::Player(changed)]
        );
        drop(rx);
        assert!(
            !present_to_window(&window, &events, changed, &mut last),
            "a window that went away is reported"
        );
    }

    #[test]
    fn a_window_attached_before_enter_world_learns_the_player_when_the_session_does() {
        // OpenAC LiveSessionHost: choosing the character hands the window the
        // player identity (`SetPlayerIdentity`), and a generation reset
        // clears it; here each change is a fresh snapshot.
        use crate::world::World;
        use crate::world::tests::{create, position, stamps};
        let mut world = World::new();
        // Attached at character selection: no player yet.
        let mut presented = world.player_guid();
        assert_eq!(identity_refresh(&world, &mut presented), None);

        world.set_player(0x5000_000A);
        world.apply(&create(
            0x5000_000A,
            stamps(1, 1),
            Some(position(0xA9B4_0001, 10.0)),
        ));
        let snapshot = identity_refresh(&world, &mut presented).expect("the player became known");
        assert_eq!(snapshot.player_guid, Some(0x5000_000A));
        assert!(snapshot.objects.iter().any(|o| o.guid == 0x5000_000A));
        assert_eq!(identity_refresh(&world, &mut presented), None, "once");

        // Logged off to selection: the world is forgotten, and so is the
        // window's copy.
        world.reset_session();
        let snapshot = identity_refresh(&world, &mut presented).expect("the player left");
        assert_eq!(snapshot.player_guid, None);
        assert!(snapshot.objects.is_empty());
    }

    fn config(enter_world: bool, await_character_selection: bool) -> SessionConfig {
        SessionConfig {
            server: "127.0.0.1:9000".parse().unwrap(),
            account: "testaccount2".into(),
            password: String::new(),
            character: CharacterSelector::FirstAvailable,
            loss: LossOptions::default(),
            enter_world,
            await_character_selection,
            data_directory: None,
            tables: None,
            content: None,
            diagnostics: NetDiagnostics::default(),
            login: LoginAutomation::default(),
            plugins: None,
        }
    }

    #[test]
    fn a_session_without_a_character_waits_at_selection() {
        // OpenAC `LiveSessionController.StartAsync`: with
        // `AwaitCharacterSelection` and no character, the session stops at
        // character selection ("awaiting character selection before
        // EnterWorld"); with a character it enters it; a probe ends.
        let waiting = config(true, true);
        assert_eq!(at_selection(&waiting, false, false), AtSelection::Wait);
        assert_eq!(at_selection(&waiting, true, false), AtSelection::Nothing);
        assert!(!direct_launch(&waiting, false, false));
        assert!(!direct_launch(&waiting, true, false));

        let entering = config(true, false);
        assert_eq!(at_selection(&entering, false, false), AtSelection::Enter);
        assert_eq!(at_selection(&entering, true, false), AtSelection::Nothing);
        assert_eq!(at_selection(&entering, false, true), AtSelection::Nothing);
        assert!(direct_launch(&entering, false, false));
        assert!(direct_launch(&entering, true, true));
        assert!(
            !direct_launch(&entering, true, false),
            "after a log off the player chooses"
        );

        assert_eq!(
            at_selection(&config(false, false), false, false),
            AtSelection::EndProbe
        );
        assert!(!direct_launch(&config(false, false), false, false));
    }

    #[test]
    fn a_session_that_returns_ends_normally() {
        let h = handle(|| {});
        wait_finished(&h);
        assert_eq!(h.join_quarantining(), SessionEnd::Finished);
    }
}

#[cfg(test)]
#[path = "runner_test_server.rs"]
mod test_server;

#[cfg(test)]
#[path = "runner_boot_tests.rs"]
mod boot_tests;

#[cfg(test)]
#[path = "runner_plugin_tests.rs"]
mod plugin_tests;

#[cfg(test)]
#[path = "runner_relogin_tests.rs"]
mod relogin_tests;
