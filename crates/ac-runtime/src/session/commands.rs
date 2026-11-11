//! What a host or a window sends a running session, and what the session
//! sends an attached window: the command and presentation stream's types.
//!
//! They are plain data with no session behind them, so a window that only
//! draws a session (in this process, over a network link, in a browser)
//! builds them without the session runner.

use std::sync::mpsc::Sender;
use std::time::{Duration, Instant};

use super::CharacterSelector;
use super::creation::CreationCommand;
use super::generation::Generation;
use crate::communication::{ChatChannelKind, ChatLine, ClientConfirmation, JournalCommand};
use crate::gameplay::GameplayCommand;
use crate::inventory::ItemCommand;
use crate::world::{ObjectDelta, PlayerView, WorldSnapshot};

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
    /// Reply with [`HostEvent::CharacterSelection`](super::HostEvent::CharacterSelection).
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
    /// Reply with [`HostEvent::Contracts`](super::HostEvent::Contracts).
    ListContracts,
    /// Change the journal.
    Journal(JournalCommand),
    /// Reply with [`HostEvent::Journal`](super::HostEvent::Journal).
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
    /// Reply with [`HostEvent::CharacterOptions`](super::HostEvent::CharacterOptions).
    ShowCharacterOptions,
    /// An item request (use, pick up, move, wield, appraise, buy, trade, ...).
    Item(ItemCommand),
    /// Reply with [`HostEvent::Inventory`](super::HostEvent::Inventory).
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
    /// A remote presentation attaches (a window shown somewhere else, over
    /// a link that may stall or drop): it is fed as an attached window is
    /// and replaces one, but the session never waits for it. No portal
    /// arrival and no login is held for it, and a session whose server
    /// falls silent logs in again as it does with nothing attached.
    AttachRemote(Sender<PresentationEvent>),
    /// The window, or the remote presentation, detaches.
    Detach,
    /// Log the character off and end the session.
    Shutdown,
    /// Reply with [`HostEvent::Objects`](super::HostEvent::Objects): every object the session knows.
    ListObjects,
    /// The player answered a question the client or the server asked
    /// ([`HostEvent::Confirmation`](super::HostEvent::Confirmation)).
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
    /// Reply with [`HostEvent::Corpses`](super::HostEvent::Corpses): the corpses within this many
    /// metres.
    ListCorpses(f32),
    /// Reply with [`HostEvent::GameTime`](super::HostEvent::GameTime).
    GameTime,
    /// Spend experience or skill credits (the character window).
    Character(crate::character::CharacterCommand),
    /// Ping the server (the link-status panel); the answer's round trip
    /// shows in the next [`HostEvent::LinkStatus`](super::HostEvent::LinkStatus).
    Ping,
    /// Reply with [`HostEvent::LinkStatus`](super::HostEvent::LinkStatus).
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
    /// An object's particles go at once (the client changed its body).
    DestroyParticles(u32),
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
