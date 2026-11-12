//! The console both front ends share: which typed lines the console answers
//! itself, which go to a session as chat, and how chat is printed.
//!
//! Everything that is not one of the console's own verbs is a line typed
//! into the session's chat box, routed by the runtime exactly as a window's
//! chat box routes it (`/say`, `/tell`, `/r`, channels, client commands,
//! `@` server commands, plain speech).
//!
//! The item verbs (`/inv`, `/use`, `/appraise`, `/move`, ...) and the
//! option verbs (`/options`, `/option`, `/saveoptions`) become session
//! commands; see [`HELP`] and `content/docs/launch-options.md`.

use crate::stdio::errln;
use std::borrow::Cow;

use ac_host::console_kind::ConsoleLineKind;
use ac_runtime::communication::{CLIENT_LOCAL_TAG, ChatLine, ContractSummary, JournalPage};
use ac_runtime::gameplay::combat::{CombatAttackCommand, InputActivation};
use ac_runtime::gameplay::{
    AllegianceCommand, AllegianceMemberView, AttackHeight, BookCommand, CombatMode,
    FellowshipCommand, GameTables, GameplayCommand, GameplayEvent,
};
use ac_runtime::inventory::{
    InventoryView, ItemCommand, ItemSummary, ObjectRef, VendorCommand, VendorViewReason,
};
use ac_runtime::navigation::NavigationEvent;
use ac_runtime::session::creation::{
    AppearanceSlot, ChargenAttribute, CreationCommand, CreationSnapshot, ShadeSlot,
};
use ac_runtime::session::selection::{SelectionOperation, SelectionSnapshot};
use ac_runtime::session::{
    CharacterSelector, HostEvent, LifecycleCommand, Relogin, SessionCommand,
};

/// What a typed line asks for. `session` is the session a line was
/// addressed to (`@id ...` or `/verb id`), `None` for the default one.
#[derive(Debug, Clone, PartialEq)]
pub enum ConsoleInput<'a> {
    /// Nothing typed.
    Empty,
    /// Log off and end (one session, or all).
    Quit {
        /// Which session.
        session: Option<&'a str>,
    },
    /// List the sessions.
    Sessions,
    /// Choose the session unaddressed lines go to (none: say which).
    Session(Option<&'a str>),
    /// List the objects a session knows.
    Objects {
        /// Which session.
        session: Option<&'a str>,
    },
    /// Print a session's game time.
    Time {
        /// Which session.
        session: Option<&'a str>,
    },
    /// Open the window on a session.
    Window {
        /// Which session.
        session: Option<&'a str>,
    },
    /// Close the window.
    Close,
    /// Save a screenshot of the window.
    Screenshot(&'a str),
    /// Print a session's quest tracker.
    Contracts {
        /// Which session.
        session: Option<&'a str>,
    },
    /// Print a session's journal.
    Journal {
        /// Which session.
        session: Option<&'a str>,
    },
    /// An item or option verb.
    Command {
        /// Which session.
        session: Option<&'a str>,
        /// The request.
        command: ConsoleCommand,
    },
    /// A verb used wrongly: how to use it.
    Usage(&'static str),
    /// A chat line for a session.
    Chat {
        /// Which session.
        session: Option<&'a str>,
        /// The line as typed.
        line: &'a str,
    },
}

/// Read one typed line. `sessions` are the ids the process runs; with two
/// or more, `@id line` sends one line to that session (an `@` word that is
/// no session id is a server command and stays in the line).
#[must_use]
pub fn parse<'a>(raw: &'a str, sessions: &[&str]) -> ConsoleInput<'a> {
    let mut addressed = None;
    let mut line = raw.trim();
    if sessions.len() >= 2
        && let Some(rest) = line.strip_prefix('@')
    {
        let (id, after) = split_word(rest);
        if sessions.contains(&id) {
            addressed = Some(id);
            line = after;
        }
    }
    if line.is_empty() {
        return ConsoleInput::Empty;
    }
    let (verb, argument) = split_word(line);
    let target = |argument: &'a str| {
        if argument.is_empty() {
            addressed
        } else {
            Some(argument)
        }
    };
    let own = |name: &str| verb.len() == name.len() + 1 && verb[1..].eq_ignore_ascii_case(name);
    if !verb.starts_with('/') {
        return ConsoleInput::Chat {
            session: addressed,
            line,
        };
    }
    if own("quit") {
        ConsoleInput::Quit {
            session: target(argument),
        }
    } else if own("sessions") {
        ConsoleInput::Sessions
    } else if own("session") {
        ConsoleInput::Session((!argument.is_empty()).then_some(argument))
    } else if own("objects") {
        ConsoleInput::Objects {
            session: target(argument),
        }
    } else if own("time") {
        ConsoleInput::Time {
            session: target(argument),
        }
    } else if own("window") {
        ConsoleInput::Window {
            session: target(argument),
        }
    } else if own("close") {
        ConsoleInput::Close
    } else if own("screenshot") {
        ConsoleInput::Screenshot(argument)
    } else if own("contracts") {
        ConsoleInput::Contracts {
            session: target(argument),
        }
    } else if own("journal") {
        ConsoleInput::Journal {
            session: target(argument),
        }
    } else if let Some(input) = crate::movement_verbs::parse(&verb[1..], argument) {
        match input {
            Ok(command) => ConsoleInput::Command {
                session: addressed,
                command: ConsoleCommand::Movement(command),
            },
            Err(usage) => ConsoleInput::Usage(usage),
        }
    } else if let Some(input) = item_verb(&verb[1..], argument)
        .or_else(|| gameplay_verb(&verb[1..], argument))
        .or_else(|| session_verb(&verb[1..], argument))
    {
        match input {
            Ok(command) => ConsoleInput::Command {
                session: addressed,
                command,
            },
            Err(usage) => ConsoleInput::Usage(usage),
        }
    } else {
        ConsoleInput::Chat {
            session: addressed,
            line,
        }
    }
}

/// A line typed at the console for a session: it goes through the one chat
/// entry, as the chat box's Enter sends it (the entry's talk focus, the
/// recall history); `None` (an empty line) sends the staged draft, if any.
#[must_use]
pub fn typed_line(line: Option<&str>) -> SessionCommand {
    SessionCommand::ChatEntry(ac_runtime::communication::ChatEntryCommand::Submit(
        line.map(str::to_owned),
    ))
}

/// The first whitespace-separated word and the rest, trimmed.
fn split_word(text: &str) -> (&str, &str) {
    let text = text.trim_start();
    match text.find(char::is_whitespace) {
        Some(at) => (&text[..at], text[at..].trim()),
        None => (text, ""),
    }
}

/// In front of every line the console says about itself, never in front of
/// a line of chat.
pub const NOTICE_PREFIX: &str = "-- ";

/// `line` as the console writes it: when the launcher asked for marks
/// (`marks`), every line of it behind the mark of its `kind`; a line of no
/// kind (the client's log) and every line when marks were not asked for are
/// written as they are.
#[must_use]
pub fn marked(kind: Option<ConsoleLineKind>, line: &str, marks: bool) -> Cow<'_, str> {
    match kind.filter(|_| marks) {
        Some(kind) => Cow::Owned(ac_host::console_kind::mark_lines(kind, line)),
        None => Cow::Borrowed(line),
    }
}

/// How long after a command typed on a session's console that session's
/// answers are shown as answers: the replies a typed command gets come
/// within a round trip to the server, and the same events caused by a
/// plugin or a bot policy at any other time are the client's log.
pub const ANSWER_WINDOW: std::time::Duration = std::time::Duration::from_secs(5);

/// Whether `event` is the kind of event that answers a command (a list, a
/// view, a refusal, a move's end). It is shown as an answer only while a
/// command typed on its session's console waits for one (see
/// [`AnswerWindow`]); a plugin's or a policy's requests make the same
/// events. An appraisal answers only the player's own question (a plugin's
/// quiet identify does not open the examination window).
#[must_use]
pub fn answer_kind(event: &HostEvent) -> bool {
    use ac_runtime::movement::MovementEvent;
    match event {
        HostEvent::Appraisal(view) => view.opens_window,
        HostEvent::Objects(_)
        | HostEvent::GameTime(_)
        | HostEvent::Contracts(_)
        | HostEvent::Journal { .. }
        | HostEvent::Inventory(_)
        | HostEvent::Vendor(_)
        | HostEvent::Trade(_)
        | HostEvent::ItemMana(_)
        | HostEvent::OpenSalvage(_)
        | HostEvent::BeginGame(_)
        | HostEvent::LinkStatus(_)
        | HostEvent::Corpses(_)
        | HostEvent::CharacterOptions(_)
        | HostEvent::CommandRefused(_)
        | HostEvent::CharacterSelection(_)
        | HostEvent::CharacterCreation(_)
        | HostEvent::Movement(
            MovementEvent::Status(_) | MovementEvent::Refused(_) | MovementEvent::MoveEnded { .. },
        ) => true,
        HostEvent::Gameplay(event) => matches!(
            event.as_ref(),
            GameplayEvent::Spellbook(_)
                | GameplayEvent::Enchantments(_)
                | GameplayEvent::Character(_)
                | GameplayEvent::Combat(_)
                | GameplayEvent::Refused(_)
                | GameplayEvent::Selection { .. }
                | GameplayEvent::CombatMode(_)
                | GameplayEvent::Vitae(_)
                | GameplayEvent::Book(_)
                | GameplayEvent::Fellowship(_)
                | GameplayEvent::Allegiance(_)
                | GameplayEvent::House(_)
                | GameplayEvent::Titles(_)
        ),
        _ => false,
    }
}

/// One session's answer window: open for [`ANSWER_WINDOW`] after the last
/// command typed on its console. The console thread opens it, the session's
/// monitor reads it; a new command moves the end on, so repeated commands
/// keep one window, never a queue.
#[derive(Debug, Default)]
pub struct AnswerWindow(std::sync::Mutex<Option<std::time::Instant>>);

impl AnswerWindow {
    /// A command was typed at `now`.
    pub fn open(&self, now: std::time::Instant) {
        if let Ok(mut until) = self.0.lock() {
            *until = Some(now + ANSWER_WINDOW);
        }
    }

    /// Whether a typed command still waits for answers at `now`.
    pub fn is_open(&self, now: std::time::Instant) -> bool {
        self.0
            .lock()
            .ok()
            .and_then(|until| *until)
            .is_some_and(|until| now < until)
    }
}

/// The kind of the console's lines for `event` at `now`: chat the main chat
/// window shows, the status box's text, an answer to a command typed within
/// the session's answer window or a question for the player, or `None` for
/// the client's log.
#[must_use]
pub fn event_kind(
    event: &HostEvent,
    answers: &AnswerWindow,
    now: std::time::Instant,
) -> Option<ConsoleLineKind> {
    match event {
        HostEvent::Chat(_) => Some(ConsoleLineKind::Chat),
        HostEvent::InterfaceText(_) => Some(ConsoleLineKind::Status),
        // The client asks the player something: shown whatever asked it.
        HostEvent::Confirmation(_) => Some(ConsoleLineKind::Reply),
        event if answer_kind(event) && answers.is_open(now) => Some(ConsoleLineKind::Reply),
        _ => None,
    }
}

/// The game tables every session of the process shares, read once from
/// `dat_dir` (else the usual install place). Without them spells have no
/// names and skills no attribute term; the console says so.
#[must_use]
pub fn game_tables(dat_dir: Option<&std::path::Path>) -> Option<std::sync::Arc<GameTables>> {
    match GameTables::open(dat_dir) {
        Ok(tables) => Some(std::sync::Arc::new(tables)),
        Err(error) => {
            errln!(
                "{}",
                notice(
                    &format!("no game tables ({error}): spells have no names"),
                    None
                )
            );
            None
        }
    }
}

/// The dats every session of the process moves in (collision, motion
/// tables), opened once from `dat_dir` (else the usual install place).
/// Without them the character does not move; the console says so.
#[must_use]
pub fn world_content(
    dat_dir: Option<&std::path::Path>,
) -> Option<std::sync::Arc<ac_runtime::movement::WorldContent>> {
    match ac_runtime::movement::WorldContent::open(dat_dir) {
        Ok(content) => Some(std::sync::Arc::new(content)),
        Err(error) => {
            errln!(
                "{}",
                notice(
                    &format!("no movement content ({error}): the character cannot move"),
                    None
                )
            );
            None
        }
    }
}

/// A chat line as the console prints it: the session (when several run),
/// the tag that stands in for the line's colour, the words.
#[must_use]
pub fn chat_line(line: &ChatLine, session: Option<&str>) -> String {
    format!("{}{}{}", session_prefix(session), line.tag(), line.text)
}

/// A chat line as the console prints it, standing in for the main chat
/// window: `None` for a text class that window's (default) filter hides;
/// with `color` (the output is a terminal) the line is written in the
/// colour the window draws it in.
#[must_use]
pub fn console_chat_line(line: &ChatLine, session: Option<&str>, color: bool) -> Option<String> {
    use ac_core::chat::{ChatWindowId, ChatWindowState};
    if !ChatWindowState::new().type_is_active(ChatWindowId::MAIN, line.log_text_type) {
        return None;
    }
    let text = chat_line(line, session);
    Some(
        match color
            .then(|| chat_color_escape(line.log_text_type))
            .flatten()
        {
            Some(escape) => format!("{escape}{text}{ANSI_RESET}"),
            None => text,
        },
    )
}

/// The escape that ends a coloured or dimmed line.
pub const ANSI_RESET: &str = "\u{1b}[0m";
/// The escape that dims a line the console says about itself.
pub const ANSI_DIM: &str = "\u{1b}[2m";

/// The 24-bit colour escape of a text class, as the chat window colours it;
/// `None` for a class the colour table does not list.
#[must_use]
pub fn chat_color_escape(log_text_type: u32) -> Option<String> {
    let color = ac_ui::panels::transcript::chat_color(log_text_type)?;
    // Rounded half to even, as .NET's MathF.Round.
    let channel = |value: f32| (value.clamp(0.0, 1.0) * 255.0).round_ties_even() as u8;
    Some(format!(
        "\u{1b}[38;2;{};{};{}m",
        channel(color.r),
        channel(color.g),
        channel(color.b)
    ))
}

/// Client-local text, which a window shows in its overlay.
#[must_use]
pub fn interface_text(text: &str, session: Option<&str>) -> String {
    format!("{}{CLIENT_LOCAL_TAG}{text}", session_prefix(session))
}

/// A line about the session itself.
#[must_use]
pub fn notice(text: &str, session: Option<&str>) -> String {
    format!("{NOTICE_PREFIX}{}{text}", session_prefix(session))
}

fn session_prefix(session: Option<&str>) -> String {
    session.map_or_else(String::new, |id| format!("[{id}] "))
}

/// The quest tracker as notice lines, one contract per line.
#[must_use]
pub fn contract_lines(contracts: &[ContractSummary], session: Option<&str>) -> Vec<String> {
    let mut lines = vec![notice(&format!("{} contracts", contracts.len()), session)];
    for c in contracts {
        let name = if c.name.is_empty() {
            format!("contract {}", c.contract_id)
        } else {
            c.name.clone()
        };
        let shown = if c.displayed { " (displayed)" } else { "" };
        lines.push(notice(&format!("  {name}: {}{shown}", c.progress), session));
    }
    lines
}

/// The journal as notice lines, one page per line.
#[must_use]
pub fn journal_lines(
    pages: &[JournalPage],
    current_page: usize,
    session: Option<&str>,
) -> Vec<String> {
    let mut lines = vec![notice(
        &format!(
            "journal: {} pages, reading page {current_page}",
            pages.len()
        ),
        session,
    )];
    for (i, page) in pages.iter().enumerate() {
        lines.push(notice(
            &format!(
                "  {}. [{}] {}: {}",
                i + 1,
                page.label,
                page.title,
                page.notes
            ),
            session,
        ));
    }
    lines
}

/// What an item or option verb asks a session for.
#[derive(Debug, Clone, PartialEq)]
pub enum ConsoleCommand {
    /// List the inventory.
    ListInventory,
    /// List the character options.
    ShowOptions,
    /// Save the options block.
    SaveOptions,
    /// Change one character option.
    SetOption {
        /// The option id.
        option: u32,
        /// On or off.
        value: bool,
    },
    /// An item request.
    Item(ItemCommand),
    /// A selection, magic, combat, book, fellowship, allegiance or house
    /// request.
    Gameplay(GameplayCommand),
    /// Character selection, creation, log off or reconnect.
    Lifecycle(LifecycleCommand),
    /// A movement request (walk, run, turn, jump, stop, ...).
    Movement(ac_runtime::movement::MovementCommand),
    /// Answer the question the client asked (`/die`'s).
    AnswerConfirmation(bool),
    /// List the corpses within this many metres.
    ListCorpses(f32),
    /// Ping the server (the link panel's round trip).
    Ping,
    /// Print the link's numbers.
    LinkStatus,
    /// Let go of an object the server stopped talking about.
    DismissGhost(u32),
}

impl ConsoleCommand {
    /// The session command it stands for.
    #[must_use]
    pub fn into_session_command(self) -> SessionCommand {
        match self {
            Self::ListInventory => SessionCommand::ListInventory,
            Self::ShowOptions => SessionCommand::ShowCharacterOptions,
            Self::SaveOptions => SessionCommand::SaveCharacterOptions,
            Self::SetOption { option, value } => {
                SessionCommand::SetCharacterOption { option, value }
            }
            Self::Item(command) => SessionCommand::Item(command),
            Self::Gameplay(command) => SessionCommand::Gameplay(command),
            Self::Lifecycle(command) => SessionCommand::Lifecycle(command),
            Self::Movement(command) => SessionCommand::Movement(command),
            // The open question: `/die`'s or the server's, whichever was
            // asked last.
            Self::AnswerConfirmation(accepted) => {
                SessionCommand::AnswerOpenConfirmation { accepted }
            }
            Self::ListCorpses(metres) => SessionCommand::ListCorpses(metres),
            Self::Ping => SessionCommand::Ping,
            Self::LinkStatus => SessionCommand::ShowLinkStatus,
            Self::DismissGhost(guid) => SessionCommand::DismissGhost(guid),
        }
    }
}

/// Split arguments at spaces; a name with spaces goes in double quotes.
fn words(text: &str) -> Vec<String> {
    let mut words = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    for c in text.chars() {
        match c {
            '"' => quoted = !quoted,
            c if c.is_whitespace() && !quoted => {
                if !current.is_empty() {
                    words.push(std::mem::take(&mut current));
                }
            }
            c => current.push(c),
        }
    }
    if !current.is_empty() {
        words.push(current);
    }
    words
}

/// The session command an item or option verb asks for: `None` for a verb
/// that is not one of them, `Err` with the usage for one used wrongly.
fn item_verb(verb: &str, argument: &str) -> Option<Result<ConsoleCommand, &'static str>> {
    let verb = verb.to_ascii_lowercase();
    let w = words(argument);
    let object = |i: usize| w.get(i).map(|s| ObjectRef::parse(s));
    let number = |i: usize, default: u32| -> Option<u32> {
        match w.get(i) {
            None => Some(default),
            Some(s) => match s.strip_prefix("0x") {
                Some(hex) => u32::from_str_radix(hex, 16).ok(),
                None => s.parse().ok(),
            },
        }
    };
    let item = |command: ItemCommand| Ok(ConsoleCommand::Item(command));
    let usage: &'static str = match verb.as_str() {
        "inv" => return Some(Ok(ConsoleCommand::ListInventory)),
        "ping" => return Some(Ok(ConsoleCommand::Ping)),
        "link" => return Some(Ok(ConsoleCommand::LinkStatus)),
        "options" => return Some(Ok(ConsoleCommand::ShowOptions)),
        "saveoptions" => return Some(Ok(ConsoleCommand::SaveOptions)),
        "use" => "usage: /use [item|guid|me] (none: the selection)",
        "useon" => "usage: /useon <item> <target>",
        "appraise" => "usage: /appraise [object] (none: the selection)",
        "pickup" => "usage: /pickup [object [main]] (none: the selection)",
        "move" => "usage: /move <item> <container|me> [amount] [slot]",
        "merge" => "usage: /merge <from stack> <to stack> [amount]",
        "drop" => "usage: /drop <item> [amount]",
        "give" => "usage: /give <item> <target> [amount]",
        "wield" => "usage: /wield <item> [slot bits]",
        "unwield" => "usage: /unwield <item>",
        "inscribe" => "usage: /inscribe <item> [\"text\"] (no text clears it)",
        "buy" => "usage: /buy <listed item> [amount]",
        "sell" => "usage: /sell <item> [amount]",
        "xtrade" => "usage: /xtrade open <player> | add <item> | accept | decline | reset | close",
        "vendor" => "usage: /vendor [close]",
        "buylist" => {
            "usage: /buylist [add <listed item> [amount] | remove <listed item> [amount] | clear | buy]"
        }
        "selllist" => "usage: /selllist [add <item> | remove <item> | clear | sell]",
        "confirm" => "usage: /confirm yes|no",
        "corpses" => "usage: /corpses [metres]",
        "option" => "usage: /option <name|id> on|off",
        "salvage" => "usage: /salvage <tool> <item> [item ...]",
        "place" => "usage: /place <item> [onto <object>] [amount]",
        "dismiss" => "usage: /dismiss <0xguid>",
        "mana" => "usage: /mana <item>",
        _ => return None,
    };
    let vendor = |command: VendorCommand| Some(item(ItemCommand::Vendor(command)));
    let signed = |i: usize, default: i32| -> Option<i32> {
        w.get(i).map_or(Some(default), |s| s.parse().ok())
    };
    let first = w.first().map(|s| s.to_ascii_lowercase());
    let command = match (verb.as_str(), w.len()) {
        // Without an object: the Use, Examine and Pick-up keys (the
        // selection, else the mode that waits for a click).
        ("use", 0) => Some(item(ItemCommand::UseSelected)),
        ("appraise", 0) => Some(item(ItemCommand::ExamineSelected)),
        ("pickup", 0) => Some(item(ItemCommand::PickUpSelected)),
        ("use", 1) => object(0).map(ItemCommand::Use).map(item),
        ("useon", 2) => object(0).zip(object(1)).map(|(item_ref, target)| {
            item(ItemCommand::UseOn {
                item: item_ref,
                target,
            })
        }),
        ("appraise", 1) => object(0).map(ItemCommand::Appraise).map(item),
        ("pickup", 1) => object(0).map(ItemCommand::PickUp).map(item),
        ("pickup", 2) if w[1].eq_ignore_ascii_case("main") => {
            object(0).map(ItemCommand::PickUpToMainPack).map(item)
        }
        ("move", 2..=4) => object(0)
            .zip(object(1))
            .zip(number(2, 0).zip(number(3, 0)))
            .map(|((item_ref, container), (amount, slot))| {
                item(ItemCommand::MoveToContainer {
                    item: item_ref,
                    container,
                    slot: i32::try_from(slot).unwrap_or(0),
                    amount,
                    join_stack: false,
                })
            }),
        ("merge", 2 | 3) => object(0)
            .zip(object(1))
            .zip(number(2, 0))
            .map(|((from, to), amount)| item(ItemCommand::Merge { from, to, amount })),
        ("drop", 1 | 2) => object(0).zip(number(1, 0)).map(|(item_ref, amount)| {
            item(ItemCommand::Drop {
                item: item_ref,
                amount,
            })
        }),
        ("give", 2 | 3) => {
            object(0)
                .zip(object(1))
                .zip(number(2, 0))
                .map(|((item_ref, target), amount)| {
                    item(ItemCommand::Give {
                        item: item_ref,
                        target,
                        amount,
                    })
                })
        }
        ("wield", 1 | 2) => object(0).zip(number(1, 0)).map(|(item_ref, location)| {
            item(ItemCommand::Wield {
                item: item_ref,
                location,
            })
        }),
        ("unwield", 1) => object(0).map(ItemCommand::Unwield).map(item),
        ("salvage", 2..) => object(0).map(|tool| {
            item(ItemCommand::Salvage {
                tool,
                items: w[1..].iter().map(|s| ObjectRef::parse(s)).collect(),
            })
        }),
        ("mana", 1) => object(0).map(ItemCommand::QueryMana).map(item),
        ("dismiss", 1) => match object(0) {
            Some(ObjectRef::Guid(guid)) => Some(Ok(ConsoleCommand::DismissGhost(guid))),
            _ => None,
        },
        ("place", 1..=4) => {
            let onto = w.get(1).is_some_and(|s| s.eq_ignore_ascii_case("onto"));
            let (target, amount_at) = if onto { (object(2), 3) } else { (None, 1) };
            let fits = if onto { w.len() >= 3 } else { w.len() <= 2 };
            if fits && w.len() <= amount_at + 1 {
                object(0)
                    .zip(number(amount_at, 0))
                    .map(|(item_ref, amount)| {
                        item(ItemCommand::Place {
                            item: item_ref,
                            target,
                            amount,
                        })
                    })
            } else {
                None
            }
        }
        ("inscribe", 1..) => object(0).map(|item_ref| {
            item(ItemCommand::Inscribe {
                item: item_ref,
                text: w[1..].join(" "),
            })
        }),
        ("buy", 1 | 2) | ("sell", 1 | 2) => object(0).zip(number(1, 1)).map(|(item_ref, n)| {
            let list = vec![(item_ref, i32::try_from(n).unwrap_or(i32::MAX))];
            item(if verb == "buy" {
                ItemCommand::Buy(list)
            } else {
                ItemCommand::Sell(list)
            })
        }),
        ("xtrade", _) => match (w.first().map(|s| s.to_ascii_lowercase()), w.len()) {
            (Some(s), 2) if s == "open" => object(1).map(ItemCommand::OpenTrade).map(item),
            (Some(s), 2) if s == "add" => object(1).map(ItemCommand::AddToTrade).map(item),
            (Some(s), 1) => match s.as_str() {
                "accept" => Some(item(ItemCommand::AcceptTrade)),
                "decline" => Some(item(ItemCommand::DeclineTrade)),
                "reset" => Some(item(ItemCommand::ResetTrade)),
                "close" => Some(item(ItemCommand::CloseTrade)),
                _ => None,
            },
            _ => None,
        },
        ("vendor", 0) => vendor(VendorCommand::Show),
        ("vendor", 1) if first.as_deref() == Some("close") => vendor(VendorCommand::Close),
        ("buylist", 0) => vendor(VendorCommand::Show),
        ("buylist", 2 | 3) if first.as_deref() == Some("add") => {
            object(1).zip(signed(2, 0)).and_then(|(item_ref, amount)| {
                vendor(VendorCommand::AddToBuyList {
                    item: item_ref,
                    amount,
                })
            })
        }
        ("buylist", 2 | 3) if first.as_deref() == Some("remove") => {
            object(1).zip(signed(2, -1)).and_then(|(item_ref, amount)| {
                vendor(VendorCommand::RemoveFromBuyList {
                    item: item_ref,
                    amount,
                })
            })
        }
        ("buylist", 1) => match first.as_deref() {
            Some("clear") => vendor(VendorCommand::ClearBuyList),
            Some("buy") => vendor(VendorCommand::BuyAll),
            _ => None,
        },
        ("selllist", 0) => vendor(VendorCommand::Show),
        ("selllist", 2) if first.as_deref() == Some("add") => {
            object(1).and_then(|item_ref| vendor(VendorCommand::AddToSellList(item_ref)))
        }
        ("selllist", 2) if first.as_deref() == Some("remove") => {
            object(1).and_then(|item_ref| vendor(VendorCommand::RemoveFromSellList(item_ref)))
        }
        ("selllist", 1) => match first.as_deref() {
            Some("clear") => vendor(VendorCommand::ClearSellList),
            Some("sell") => vendor(VendorCommand::SellAll),
            _ => None,
        },
        ("confirm", 1) => match first.as_deref() {
            Some("yes" | "y") => Some(Ok(ConsoleCommand::AnswerConfirmation(true))),
            Some("no" | "n") => Some(Ok(ConsoleCommand::AnswerConfirmation(false))),
            _ => None,
        },
        ("corpses", 0) => Some(Ok(ConsoleCommand::ListCorpses(100.0))),
        ("corpses", 1) => w[0]
            .parse::<f32>()
            .ok()
            .filter(|m| *m > 0.0)
            .map(|m| Ok(ConsoleCommand::ListCorpses(m))),
        ("option", 2) => {
            let value = match w[1].to_ascii_lowercase().as_str() {
                "on" | "true" | "1" => Some(true),
                "off" | "false" | "0" => Some(false),
                _ => None,
            };
            let option = ac_runtime::character::option_by_name(&w[0])
                .map(|e| e.id)
                .or_else(|| number(0, 0).filter(|id| ac_runtime::character::option(*id).is_some()));
            option
                .zip(value)
                .map(|(option, value)| Ok(ConsoleCommand::SetOption { option, value }))
        }
        _ => None,
    };
    Some(command.unwrap_or(Err(usage)))
}

/// The session command a selection, magic, combat, book, fellowship,
/// allegiance or house verb asks for: `None` for another verb, `Err` with
/// the usage for one used wrongly. Retail's own commands (`/house`,
/// `/allegiance`, the `/f` channel family) keep their names; these verbs
/// avoid them.
#[allow(clippy::too_many_lines)]
/// `/target …`: the keyboard's selection keys. Alone, the nearest monster;
/// a kind steps through that kind (`next`, `previous`, else the closest);
/// `next` or `previous` alone steps through people.
fn target_verb(w: &[String]) -> Option<GameplayCommand> {
    use GameplayCommand as G;
    use ac_runtime::gameplay::selection_cycle::{
        SelectionCycleDirection as D, SelectionCycleKind as K,
    };
    let lower: Vec<String> = w.iter().map(|s| s.to_ascii_lowercase()).collect();
    let words: Vec<&str> = lower.iter().map(String::as_str).collect();
    let direction = |word: Option<&&str>| match word.copied() {
        None => Some(D::Closest),
        Some("next") => Some(D::Next),
        Some("previous" | "prev") => Some(D::Previous),
        Some(_) => None,
    };
    let cycle = |kind, rest: &[&str]| {
        if rest.len() > 1 {
            return None;
        }
        direction(rest.first()).map(|direction| G::CycleSelection { kind, direction })
    };
    match words.as_slice() {
        [] => Some(G::SelectNearestMonster),
        ["back"] => Some(G::SelectPrevious),
        ["self" | "me"] => Some(G::SelectSelf),
        ["attacker"] => Some(G::SelectLastAttacker),
        ["fellow"] => Some(G::CycleFellow { previous: false }),
        ["fellow", "next"] => Some(G::CycleFellow { previous: false }),
        ["fellow", "previous" | "prev"] => Some(G::CycleFellow { previous: true }),
        ["next" | "previous" | "prev"] => cycle(K::Player, &words),
        ["corpse", "use", rest @ ..] if rest.len() <= 1 => match direction(rest.first())? {
            D::Previous => None,
            direction => Some(G::UseUnopenedCorpse(direction)),
        },
        ["corpse", rest @ ..] => match cycle(K::UnopenedCorpse, rest)? {
            // The keys step forward through corpses only.
            G::CycleSelection {
                direction: D::Previous,
                ..
            } => None,
            command => Some(command),
        },
        ["monster", rest @ ..] => cycle(K::Monster, rest),
        ["player", rest @ ..] => cycle(K::Player, rest),
        ["item", rest @ ..] => cycle(K::Item, rest),
        ["compass", rest @ ..] => cycle(K::CompassItem, rest),
        _ => None,
    }
}

fn gameplay_verb(verb: &str, argument: &str) -> Option<Result<ConsoleCommand, &'static str>> {
    use GameplayCommand as G;
    let verb = verb.to_ascii_lowercase();
    let w = words(argument);
    let first = w.first().map(|s| s.to_ascii_lowercase());
    let rest = |from: usize| w.get(from..).map(|r| r.join(" ")).unwrap_or_default();
    let object = |i: usize| w.get(i).map(|s| ObjectRef::parse(s));
    let on_off = |i: usize| match w.get(i).map(|s| s.to_ascii_lowercase()).as_deref() {
        Some("on" | "true" | "1") => Some(true),
        Some("off" | "false" | "0") => Some(false),
        _ => None,
    };
    let ok = |command: G| Ok(ConsoleCommand::Gameplay(command));
    let usage: &'static str = match verb.as_str() {
        "select" => "usage: /select <object|me>",
        "deselect" => return Some(ok(G::ClearSelection)),
        "target" => {
            "usage: /target [monster | player | item | compass | corpse [use]] [next | previous], \
             /target self | attacker | back | fellow [previous]"
        }
        "spells" => {
            let filter = (!argument.trim().is_empty()).then(|| argument.trim().to_owned());
            return Some(ok(G::ListSpells(filter)));
        }
        "enchantments" | "buffs" => return Some(ok(G::ShowEnchantments)),
        "sheet" => return Some(ok(G::ShowCharacter)),
        "vitae" => return Some(ok(G::ShowVitae)),
        "cast" => "usage: /cast <spell id|\"spell name\"> [target]",
        "combat" => "usage: /combat [peace|melee|missile|magic|status]",
        "attack" => "usage: /attack [high|medium|low] [target]",
        "power" => "usage: /power <0..1>",
        "stopattack" => {
            return Some(ok(G::AttackInput(
                CombatAttackCommand::AbortForMovement,
                InputActivation::Press,
            )));
        }
        "book" => "usage: /book [page <n> | write <text> | close]",
        "fship" => {
            "usage: /fship [create <name> [share] | recruit <p> | dismiss <p> | quit | disband | leader <p> | open on|off | vitals on|off]"
        }
        "alleg" => "usage: /alleg [swear <p> | break <p> | kick <p> | info <name> | update on|off]",
        "myhouse" => "usage: /myhouse [query]",
        "titles" => "usage: /titles [set <title id>]",
        _ => return None,
    };
    let command = match (verb.as_str(), first.as_deref(), w.len()) {
        ("select", _, 1) => object(0).map(G::Select).map(ok),
        ("cast", _, 1 | 2) => Some(ok(G::Cast {
            spell: w[0].clone(),
            target: object(1),
        })),
        ("combat", None, 0) => Some(ok(G::ToggleCombat)),
        ("combat", Some(mode), 1) => match mode {
            "status" => Some(ok(G::ShowCombat)),
            "peace" => Some(ok(G::SetCombatMode(CombatMode::NON_COMBAT))),
            "melee" => Some(ok(G::SetCombatMode(CombatMode::MELEE))),
            "missile" => Some(ok(G::SetCombatMode(CombatMode::MISSILE))),
            "magic" => Some(ok(G::SetCombatMode(CombatMode::MAGIC))),
            _ => None,
        },
        ("attack", height, 0..=2) => {
            let (height, target) = match height {
                Some("high") => (AttackHeight::HIGH, 1),
                Some("medium") => (AttackHeight::MEDIUM, 1),
                Some("low") => (AttackHeight::LOW, 1),
                _ => (AttackHeight::MEDIUM, 0),
            };
            (w.len() <= target + 1).then(|| {
                ok(G::Attack {
                    height,
                    target: object(target),
                })
            })
        }
        ("power", _, 1) => w[0]
            .parse::<f32>()
            .ok()
            .filter(|p| (0.0..=1.0).contains(p))
            .map(|p| ok(G::SetAttackPower(p))),
        ("book", None, 0) => Some(ok(G::Book(BookCommand::Show))),
        ("book", Some("close"), 1) => Some(ok(G::Book(BookCommand::Close))),
        ("book", Some("page"), 2) => w[1]
            .parse::<i32>()
            .ok()
            .map(|page| ok(G::Book(BookCommand::Turn(page)))),
        ("book", Some("write"), _) => Some(ok(G::Book(BookCommand::Write(rest(1))))),
        ("fship", None, 0) => Some(ok(G::Fellowship(FellowshipCommand::Show))),
        ("fship", Some("create"), 2 | 3) => Some(ok(G::Fellowship(FellowshipCommand::Create {
            name: w[1].clone(),
            share_xp: w.get(2).is_some_and(|s| s.eq_ignore_ascii_case("share")),
        }))),
        ("fship", Some("recruit"), 2) => object(1)
            .map(FellowshipCommand::Recruit)
            .map(|c| ok(G::Fellowship(c))),
        ("fship", Some("dismiss"), 2) => object(1)
            .map(FellowshipCommand::Dismiss)
            .map(|c| ok(G::Fellowship(c))),
        ("fship", Some("leader"), 2) => object(1)
            .map(FellowshipCommand::AssignLeader)
            .map(|c| ok(G::Fellowship(c))),
        ("fship", Some("quit"), 1) => Some(ok(G::Fellowship(FellowshipCommand::Quit {
            disband: false,
        }))),
        ("fship", Some("disband"), 1) => {
            Some(ok(G::Fellowship(FellowshipCommand::Quit { disband: true })))
        }
        ("fship", Some("open"), 2) => {
            on_off(1).map(|o| ok(G::Fellowship(FellowshipCommand::SetOpen(o))))
        }
        ("fship", Some("vitals"), 2) => {
            on_off(1).map(|o| ok(G::Fellowship(FellowshipCommand::RequestVitals(o))))
        }
        ("alleg", None, 0) => Some(ok(G::Allegiance(AllegianceCommand::Show))),
        ("alleg", Some("swear"), 2) => object(1)
            .map(AllegianceCommand::Swear)
            .map(|c| ok(G::Allegiance(c))),
        ("alleg", Some("break"), 2) => object(1)
            .map(AllegianceCommand::Break)
            .map(|c| ok(G::Allegiance(c))),
        ("alleg", Some("kick"), 2) => object(1)
            .map(AllegianceCommand::Kick)
            .map(|c| ok(G::Allegiance(c))),
        ("alleg", Some("info"), 2..) => Some(ok(G::Allegiance(AllegianceCommand::Info(rest(1))))),
        ("alleg", Some("update"), 2) => {
            on_off(1).map(|o| ok(G::Allegiance(AllegianceCommand::Subscribe(o))))
        }
        ("target", _, _) => target_verb(&w).map(ok),
        ("myhouse", None, 0) => Some(ok(G::ShowHouse)),
        ("myhouse", Some("query"), 1) => Some(ok(G::QueryHouse)),
        ("titles", None, 0) => Some(ok(G::ShowTitles)),
        ("titles", Some("set"), 2) => w[1].parse::<u32>().ok().map(|id| ok(G::SetTitle(id))),
        _ => None,
    };
    Some(command.unwrap_or(Err(usage)))
}

fn percent(value: f32) -> String {
    format!("{:.0}%", value * 100.0)
}

/// The lines a gameplay event prints.
#[allow(clippy::too_many_lines)]
fn gameplay_lines(event: &GameplayEvent, session: Option<&str>) -> Vec<String> {
    let say = |text: String| notice(&text, session);
    match event {
        GameplayEvent::Selection { selected, name } => vec![say(match selected {
            Some(guid) => format!("selected 0x{guid:08X} {}", name.as_deref().unwrap_or("?")),
            None => "selection cleared".to_owned(),
        })],
        GameplayEvent::Spellbook(view) => {
            let mut lines = vec![say(format!(
                "spellbook: {} known, {} listed, filter 0x{:X}",
                view.known,
                view.spells.len(),
                view.filters
            ))];
            for s in &view.spells {
                lines.push(say(format!(
                    "  {:>5} {} ({}, level {}, mana {}){}",
                    s.spell_id,
                    if s.name.is_empty() { "?" } else { &s.name },
                    s.school,
                    s.level,
                    s.mana,
                    if s.has_components {
                        ""
                    } else {
                        " - components missing"
                    }
                )));
            }
            for (tab, bar) in view.favorites.iter().enumerate() {
                if !bar.is_empty() {
                    lines.push(say(format!("  favorites {}: {bar:?}", tab + 1)));
                }
            }
            lines
        }
        GameplayEvent::Enchantments(list) => {
            let mut lines = vec![say(format!("{} enchantments", list.len()))];
            for e in list {
                let left = e
                    .remaining_seconds
                    .map_or_else(|| "permanent".to_owned(), |s| format!("{s:.0} s left"));
                lines.push(say(format!(
                    "  {} {} (layer {}, bucket {}, key {} value {}): {left}",
                    e.spell_id,
                    if e.name.is_empty() { "?" } else { &e.name },
                    e.layer,
                    e.bucket,
                    e.stat_mod.1,
                    e.stat_mod.2
                )));
            }
            lines
        }
        GameplayEvent::Character(sheet) => {
            let mut lines = vec![say(format!("character (vitae {})", percent(sheet.vitae)))];
            for (name, current, max) in &sheet.vitals {
                lines.push(say(format!("  {name} {current}/{max}")));
            }
            for line in sheet.attributes.iter().chain(&sheet.skills) {
                lines.push(say(format!(
                    "  {} {} (base {})",
                    line.name, line.current, line.base
                )));
            }
            lines
        }
        GameplayEvent::CastCompleted(c) => vec![say(format!(
            "cast of spell {} at 0x{:08X} finished{}",
            c.spell_id,
            c.target_id,
            if c.weenie_error == 0 {
                String::new()
            } else {
                format!(" with error 0x{:04X}", c.weenie_error)
            }
        ))],
        GameplayEvent::CombatMode(mode) => {
            vec![say(format!("combat mode {}", mode.name().unwrap_or("?")))]
        }
        GameplayEvent::Combat(v) => vec![say(format!(
            "combat: mode {}, target {}, height {}, power {} (bar {}){}{}{}",
            v.mode.name().unwrap_or("?"),
            v.selected.map_or_else(
                || "none".to_owned(),
                |s| format!(
                    "0x{s:08X}{}",
                    v.selected_health
                        .map(|h| format!(" ({})", percent(h)))
                        .unwrap_or_default()
                )
            ),
            v.height.name().unwrap_or("?"),
            percent(v.desired_power),
            percent(v.power_bar),
            if v.request_in_progress {
                ", charging"
            } else {
                ""
            },
            if v.response_pending { ", swinging" } else { "" },
            if v.repeating { ", repeating" } else { "" },
        ))],
        GameplayEvent::Health { guid, percent: p } => {
            vec![say(format!("health of 0x{guid:08X}: {}", percent(*p)))]
        }
        GameplayEvent::CreatureDied(guid) => vec![say(format!("0x{guid:08X} died"))],
        GameplayEvent::LocalPlayerDied(message) => vec![say(format!("you died: {message}"))],
        GameplayEvent::Vitae(view) => {
            let s = &view.status;
            let mut lines = vec![say(format!(
                "vitae {}: {}% lost, {} experience to the next percent",
                percent(s.multiplier),
                s.lost_percent,
                s.experience_to_next_percent
            ))];
            lines.extend(
                view.text
                    .lines()
                    .filter(|l| !l.is_empty())
                    .map(|l| say(format!("  {l}"))),
            );
            lines
        }
        GameplayEvent::Book(book) => {
            if book.book_guid == 0 {
                return vec![say("no book open".to_owned())];
            }
            let mut lines = vec![say(format!(
                "book 0x{:08X} \"{}\": {} of {} pages, reading page {}{}",
                book.book_guid,
                book.inscription,
                book.pages.len(),
                book.max_num_pages,
                book.current_page,
                if book.request_pending {
                    " (waiting for the server)"
                } else {
                    ""
                }
            ))];
            if let Some(page) = usize::try_from(book.current_page)
                .ok()
                .and_then(|i| book.pages.get(i))
            {
                lines.push(say(format!(
                    "  by {}: {}",
                    page.author_name, page.page_text
                )));
            }
            lines
        }
        GameplayEvent::Fellowship(f) => {
            if !f.in_fellowship {
                return vec![say("not in a fellowship".to_owned())];
            }
            let mut lines = vec![say(format!(
                "fellowship \"{}\", leader 0x{:08X}, {} members{}{}{}",
                f.name,
                f.leader_guid,
                f.members.len(),
                if f.share_xp {
                    ", sharing experience"
                } else {
                    ""
                },
                if f.open { ", open" } else { "" },
                if f.locked { ", locked" } else { "" }
            ))];
            for m in &f.members {
                lines.push(say(format!(
                    "  {} (level {}) health {}/{}",
                    m.name, m.level, m.health.0, m.health.1
                )));
            }
            lines
        }
        GameplayEvent::Allegiance(a) => {
            if !a.has_server_seed {
                return vec![say("allegiance: not stated by the server yet".to_owned())];
            }
            let name = |m: &AllegianceMemberView| {
                format!("{}{}", m.name, if m.online { " *" } else { "" })
            };
            let mut lines = vec![say(format!(
                "allegiance \"{}\": rank {}, {} members, {} vassals",
                a.name, a.rank, a.total_members, a.total_vassals
            ))];
            if let Some(m) = &a.monarch {
                lines.push(say(format!("  monarch: {}", name(m))));
            }
            if let Some(p) = &a.patron {
                lines.push(say(format!("  patron: {}", name(p))));
            }
            for v in &a.vassals {
                lines.push(say(format!("  vassal: {}", name(v))));
            }
            lines
        }
        GameplayEvent::House(h) => h
            .lines
            .iter()
            .flat_map(|(text, _)| text.lines())
            .map(|line| say(format!("house: {line}")))
            .collect(),
        GameplayEvent::Titles(t) => vec![say(format!(
            "titles: displaying {}, earned {:?}",
            t.display_title_id, t.earned
        ))],
        GameplayEvent::Refused(text) => vec![say(text.clone())],
    }
}

fn item_line(item: &ItemSummary, indent: &str) -> String {
    let count = if item.stack_size > 1 {
        format!(" x{}", item.stack_size)
    } else {
        String::new()
    };
    let traded = if item.trade_state == 1 {
        " (in trade)"
    } else {
        ""
    };
    format!(
        "{indent}0x{:08X} {}{count} - value {}, burden {}{traded}",
        item.guid, item.name, item.value, item.burden
    )
}

fn inventory_lines(view: &InventoryView, session: Option<&str>) -> Vec<String> {
    let mut lines = vec![notice(
        &format!(
            "inventory: {} items in the main pack, {} packs, {} worn; burden {}",
            view.main_pack.len(),
            view.packs.len(),
            view.equipped.len(),
            view.burden
        ),
        session,
    )];
    for item in &view.main_pack {
        lines.push(notice(&item_line(item, "  "), session));
    }
    for pack in &view.packs {
        lines.push(notice(
            &format!(
                "{} ({} items)",
                item_line(&pack.pack, "  "),
                pack.items.len()
            ),
            session,
        ));
        for item in &pack.items {
            lines.push(notice(&item_line(item, "      "), session));
        }
    }
    for item in &view.equipped {
        lines.push(notice(
            &format!(
                "{} [worn 0x{:08X}]",
                item_line(item, "  "),
                item.equip_location
            ),
            session,
        ));
    }
    if let Some(ground) = &view.ground {
        lines.push(notice(
            &format!(
                "open container:{} ({} items)",
                item_line(&ground.pack, " "),
                ground.items.len()
            ),
            session,
        ));
        for item in &ground.items {
            lines.push(notice(&item_line(item, "  "), session));
        }
    }
    lines
}

/// The vendor's shop: the listing with each item's price when it opens or
/// is asked for, the buy and sell lists and the purse every time.
fn vendor_lines(view: &ac_runtime::inventory::VendorView, session: Option<&str>) -> Vec<String> {
    let mut lines = Vec::new();
    if matches!(
        view.reason,
        VendorViewReason::Opened | VendorViewReason::Asked
    ) {
        lines.push(notice(
            &format!(
                "vendor 0x{:08X} {}: buys at {:.2}, sells at {:.2}, {} items",
                view.vendor_guid,
                view.name.as_deref().unwrap_or("?"),
                view.profile.buy_price,
                view.profile.sell_price,
                view.items.len()
            ),
            session,
        ));
        for item in &view.items {
            let stock = if item.stack_size < 0 {
                String::new()
            } else {
                format!(", {} in stock", item.stack_size)
            };
            lines.push(notice(
                &format!(
                    "  0x{:08X} {} - {}p each{stock}",
                    item.item_guid,
                    item.name.as_deref().unwrap_or("?"),
                    view.price(item.item_guid, 1).unwrap_or(0),
                ),
                session,
            ));
        }
    } else if view.reason == VendorViewReason::Refreshed {
        lines.push(notice(
            &format!(
                "vendor 0x{:08X} {}: listing refreshed, {} items",
                view.vendor_guid,
                view.name.as_deref().unwrap_or("?"),
                view.items.len()
            ),
            session,
        ));
    }
    let l = &view.lists;
    for (text, rows) in [(&l.buying_text, &l.buying), (&l.selling_text, &l.selling)] {
        if rows.is_empty() && view.reason != VendorViewReason::Lists {
            continue;
        }
        let list = rows
            .iter()
            .map(|r| format!("{} x{} ({}p)", r.name, r.quantity, r.price))
            .collect::<Vec<_>>()
            .join(", ");
        lines.push(notice(&format!("  {text}: [{list}]"), session));
    }
    lines.push(notice(&format!("  {}", l.purse_text), session));
    lines
}

/// What the console prints for the item, vendor, trade and option events
/// (the other events have their own printers).
#[must_use]
pub fn event_lines(event: &HostEvent, session: Option<&str>) -> Vec<String> {
    match event {
        HostEvent::Movement(event) => {
            vec![notice(&crate::movement_verbs::describe(event), session)]
        }
        HostEvent::Navigation(NavigationEvent::Report(report)) => vec![notice(
            &format!(
                "walk {}: {}",
                report.sequence,
                ac_runtime::navigation::describe_walk(report)
            ),
            session,
        )],
        HostEvent::Navigation(NavigationEvent::Said(line)) => {
            vec![notice(&format!("nav: {line}"), session)]
        }
        HostEvent::Inventory(view) => inventory_lines(view, session),
        HostEvent::Appraisal(view) => {
            let r = &view.response;
            let mut lines = vec![notice(
                &format!(
                    "appraisal of 0x{:08X} {}: {}{}",
                    r.guid,
                    view.name.as_deref().unwrap_or("?"),
                    if r.success { "success" } else { "failed" },
                    if r.spell_book.is_empty() {
                        String::new()
                    } else {
                        format!(", spells {:?}", r.spell_book)
                    }
                ),
                session,
            )];
            for (name, value) in view.property_lines() {
                lines.push(notice(&format!("  {name} = {value}"), session));
            }
            if let Some(w) = &r.weapon_profile {
                lines.push(notice(
                    &format!(
                        "  weapon: damage {} (type 0x{:X}), speed {}, variance {:.2}",
                        w.damage, w.damage_type, w.weapon_time, w.damage_variance
                    ),
                    session,
                ));
            }
            if let Some(c) = &r.creature_profile {
                lines.push(notice(
                    &format!("  health {}/{}", c.health, c.health_max),
                    session,
                ));
            }
            lines
        }
        HostEvent::Vendor(None) => vec![notice("vendor closed", session)],
        HostEvent::OpenSalvage(tool) => vec![notice(
            &format!("salvage panel opens on 0x{tool:08X}"),
            session,
        )],
        HostEvent::BeginGame(board) => vec![notice(
            &format!("game window opens on board 0x{board:08X}"),
            session,
        )],
        HostEvent::Vendor(Some(view)) => vendor_lines(view, session),
        HostEvent::Trade(view) => {
            let names = |items: &[u32]| {
                items
                    .iter()
                    .map(|guid| {
                        format!(
                            "0x{guid:08X} {}",
                            view.item_names.get(guid).map_or("?", String::as_str)
                        )
                    })
                    .collect::<Vec<_>>()
                    .join(", ")
            };
            let accepted = |yes: bool| if yes { " (accepted)" } else { "" };
            vec![notice(
                &if view.open {
                    format!(
                        "trade with 0x{:08X} {}: mine [{}]{}, theirs [{}]{}",
                        view.partner_guid,
                        view.partner_name.as_deref().unwrap_or("?"),
                        names(&view.self_items),
                        accepted(view.self_accepted),
                        names(&view.partner_items),
                        accepted(view.partner_accepted),
                    )
                } else {
                    "trade closed".to_owned()
                },
                session,
            )]
        }
        HostEvent::LinkStatus(link) => vec![notice(
            &format!(
                "link: {}, last packet {:.1} s ago, loss {:.1}%, round trip {}",
                if link.connected {
                    "connected"
                } else {
                    "disconnected"
                },
                link.seconds_since_last_packet,
                link.packet_loss_percent,
                link.round_trip_seconds
                    .map_or_else(|| "unknown".to_owned(), |s| format!("{:.0} ms", s * 1000.0))
            ),
            session,
        )],
        HostEvent::ItemMana(change) => vec![notice(
            &if change.valid {
                format!(
                    "mana of 0x{:08X}: {}",
                    change.item_id,
                    percent(change.fraction)
                )
            } else {
                format!("0x{:08X} has no mana", change.item_id)
            },
            session,
        )],
        HostEvent::Confirmation(question) => vec![notice(
            &format!("{} (answer /confirm yes or /confirm no)", question.text()),
            session,
        )],
        HostEvent::Corpses(list) => {
            let mut lines = vec![notice(&format!("{} corpses", list.len()), session)];
            for c in list {
                lines.push(notice(
                    &format!(
                        "  0x{:08X} {} {:.1} m{}{}",
                        c.guid,
                        c.name,
                        c.distance,
                        if c.own { " (yours)" } else { "" },
                        if c.opened { " (opened)" } else { "" }
                    ),
                    session,
                ));
            }
            lines
        }
        HostEvent::CharacterOptions(values) => {
            let on: Vec<&str> = values.iter().filter(|(_, v)| *v).map(|(n, _)| *n).collect();
            let off: Vec<&str> = values
                .iter()
                .filter(|(_, v)| !*v)
                .map(|(n, _)| *n)
                .collect();
            vec![
                notice(&format!("options on: {}", on.join(", ")), session),
                notice(&format!("options off: {}", off.join(", ")), session),
            ]
        }
        HostEvent::CommandRefused(text) => vec![notice(text, session)],
        HostEvent::Gameplay(event) => gameplay_lines(event, session),
        HostEvent::Lifecycle(l) => vec![notice(
            &format!(
                "lifecycle: generation {} {:?}{}",
                l.generation,
                l.state,
                if l.player_guid == 0 {
                    String::new()
                } else {
                    format!(" as 0x{:08X}", l.player_guid)
                }
            ),
            session,
        )],
        HostEvent::CharacterSelection(s) => selection_lines(s, session),
        HostEvent::CharacterCreation(c) => creation_lines(c, session),
        HostEvent::Portal(p) => vec![notice(
            &format!(
                "portal: reveal {} {:?} to 0x{:08X}{}{}",
                p.generation,
                p.kind,
                p.destination_cell(),
                if p.completed { " completed" } else { "" },
                if p.cancelled { " cancelled" } else { "" }
            ),
            session,
        )],
        HostEvent::Logout(stage) => vec![notice(&format!("logout: {stage:?}"), session)],
        HostEvent::Plugin(event) => vec![notice(&crate::plugins::event_line(event), session)],
        HostEvent::Relogin(relogin) => vec![notice(&relogin_line(relogin), session)],
        HostEvent::PortalFailure(failure) => {
            vec![notice(&crate::portal_failure::summary(failure), session)]
        }
        _ => Vec::new(),
    }
}

/// A session logging in again after its server fell silent.
fn relogin_line(relogin: &Relogin) -> String {
    match relogin {
        Relogin::Scheduled { after } => format!(
            "the connection to the server was lost; logging in again in {} s",
            after.as_secs()
        ),
        Relogin::AttemptFailed { error, after } => format!(
            "logging in again failed: {error}; next attempt in {} s",
            after.as_secs()
        ),
    }
}

/// The selection screen as notice lines: one per character, the
/// highlighted one marked, then the buttons and any error.
fn selection_lines(s: &SelectionSnapshot, session: Option<&str>) -> Vec<String> {
    let mut lines = vec![notice(
        &format!(
            "characters of {} on {} ({} of {} slots, generation {}, {:?}{})",
            s.account_name,
            if s.world_name.is_empty() {
                "?"
            } else {
                &s.world_name
            },
            s.entries.len(),
            s.slot_count,
            s.generation,
            s.lifecycle,
            match s.operation {
                SelectionOperation::None => String::new(),
                other => format!(", {other:?}"),
            }
        ),
        session,
    )];
    for e in &s.entries {
        let mark = if e.character_id == s.highlighted_character_id {
            ">"
        } else {
            " "
        };
        let greyed = if e.is_pending_delete() {
            " (pending deletion)".to_owned()
        } else {
            String::new()
        };
        lines.push(notice(
            &format!(
                " {mark} #{} 0x{:08X} {}{greyed}",
                e.active_index, e.character_id, e.name
            ),
            session,
        ));
    }
    if s.pending_delete_character_id != 0 {
        lines.push(notice(
            &format!(
                "delete 0x{:08X}? /delete yes to confirm, /cancel to keep it",
                s.pending_delete_character_id
            ),
            session,
        ));
    }
    if let Some(error) = &s.error {
        lines.push(notice(&error.message, session));
    }
    lines
}

/// Character creation as notice lines.
fn creation_lines(c: &CreationSnapshot, session: Option<&str>) -> Vec<String> {
    let a = &c.attributes;
    let mut lines = vec![notice(
        &format!(
            "creation: heritage {} gender {} template {} name \"{}\" town {} \
             (attributes {}/{}/{}/{}/{}/{}, {} points and {} skill credits left){}",
            c.heritage_id,
            c.gender_key,
            if c.template == u32::MAX {
                "-".to_owned()
            } else {
                c.template.to_string()
            },
            c.name,
            c.start_area,
            a.strength,
            a.endurance,
            a.coordination,
            a.quickness,
            a.focus,
            a.self_,
            c.remaining_attribute_credits,
            c.remaining_skill_credits,
            if c.verification_pending {
                ", waiting for the server"
            } else {
                ""
            }
        ),
        session,
    )];
    if c.last_local_refusal.any() {
        lines.push(notice(
            &format!("not sent: {:?}", c.last_local_refusal),
            session,
        ));
    }
    if let Some(r) = &c.last_rejection {
        lines.push(notice(
            &format!(
                "the server refused {}: {} ({})",
                r.attempted_name, r.reason, r.raw_code
            ),
            session,
        ));
    }
    if let Some(created) = &c.last_created {
        lines.push(notice(
            &format!("created {} (0x{:08X})", created.name, created.guid),
            session,
        ));
    }
    lines
}

/// A character named on the console: `0x…` an id, `#n` the server's list
/// index, anything else a name (case ignored).
fn character_selector(text: &str) -> Option<CharacterSelector> {
    let text = text.trim().trim_matches('"');
    if text.is_empty() {
        return None;
    }
    if let Some(hex) = text.strip_prefix("0x") {
        return u32::from_str_radix(hex, 16).ok().map(CharacterSelector::Id);
    }
    if let Some(index) = text.strip_prefix('#') {
        return index.parse().ok().map(CharacterSelector::Index);
    }
    Some(CharacterSelector::Name(text.to_owned()))
}

/// The session command a character-selection, creation, log-off or
/// reconnect verb asks for: `None` for another verb, `Err` with the usage
/// for one used wrongly.
fn session_verb(verb: &str, argument: &str) -> Option<Result<ConsoleCommand, &'static str>> {
    use LifecycleCommand as L;
    let verb = verb.to_ascii_lowercase();
    let ok = |command: L| Ok(ConsoleCommand::Lifecycle(command));
    let named = || character_selector(argument);
    // These take no word: a session is addressed with `@<id>` before the
    // verb, as on OpenAC's console, so a trailing word (`/logoff h`) is
    // refused rather than silently ignored.
    let bare = |command: L, usage: &'static str| {
        if argument.trim().is_empty() {
            ok(command)
        } else {
            Err(usage)
        }
    };
    Some(match verb.as_str() {
        "chars" => bare(
            L::ShowSelection,
            "usage: /chars (another session: @<id> /chars)",
        ),
        "logoff" => bare(L::LogOff, "usage: /logoff (another session: @<id> /logoff)"),
        "reconnect" => bare(
            L::Reconnect,
            "usage: /reconnect (another session: @<id> /reconnect)",
        ),
        "cancel" => bare(L::Cancel, "usage: /cancel (another session: @<id> /cancel)"),
        "enter" => ok(L::Enter(named())),
        "restore" => ok(L::Restore(named())),
        "delete" => match argument.trim().to_ascii_lowercase().as_str() {
            "yes" => ok(L::ConfirmDelete),
            "" => Err("usage: /delete <name|#index|0xguid>, then /delete yes"),
            _ => ok(L::RequestDelete(named())),
        },
        "nextlogin" => match argument.trim().to_ascii_lowercase().as_str() {
            "" => Err("usage: /nextlogin <name|#index|0xguid> | off"),
            "off" => ok(L::SetNextLogin(None)),
            _ => ok(L::SetNextLogin(named())),
        },
        "create" => create_verb(argument).map(|c| ConsoleCommand::Lifecycle(L::Create(c))),
        _ => return None,
    })
}

/// `/create` and its sub-verbs.
fn create_verb(argument: &str) -> Result<CreationCommand, &'static str> {
    use CreationCommand as C;
    const USAGE: &str = "usage: /create [heritage <id> | gender <key|male|female> | template <n> \
        | attr <str|end|coord|quick|focus|self> <n> | lock <attr> on|off | train|spec|untrain <skill> \
        | look <slot> <n> | look hat none | shade <slot> <0..1> | town <n> | name <name> | slot <n> \
        | finish [confirm] | ack | random [look|clothes]]";
    let w = words(argument);
    let sub = w.first().map(|s| s.to_ascii_lowercase());
    let number = |i: usize| w.get(i).and_then(|s| s.parse::<u32>().ok());
    let attribute = |i: usize| {
        Some(match w.get(i)?.to_ascii_lowercase().as_str() {
            "str" | "strength" => ChargenAttribute::Strength,
            "end" | "endurance" => ChargenAttribute::Endurance,
            "coord" | "coordination" => ChargenAttribute::Coordination,
            "quick" | "quickness" => ChargenAttribute::Quickness,
            "focus" => ChargenAttribute::Focus,
            "self" => ChargenAttribute::Self_,
            _ => return None,
        })
    };
    let on_off = |i: usize| match w.get(i).map(|s| s.to_ascii_lowercase()).as_deref() {
        Some("on") => Some(true),
        Some("off") => Some(false),
        _ => None,
    };
    let command = match (sub.as_deref(), w.len()) {
        (None, 0) => Some(C::Show),
        (Some("heritage"), 2) => number(1).map(C::SelectHeritage),
        (Some("gender"), 2) => match w[1].to_ascii_lowercase().as_str() {
            "male" => Some(C::SelectGender(1)),
            "female" => Some(C::SelectGender(2)),
            _ => number(1).map(C::SelectGender),
        },
        (Some("template"), 2) => number(1).map(C::SelectTemplate),
        (Some("attr"), 3) => attribute(1)
            .zip(w[2].parse::<i32>().ok())
            .map(|(a, v)| C::SetAttribute(a, v)),
        (Some("lock"), 3) => attribute(1)
            .zip(on_off(2))
            .map(|(a, on)| C::SetAttributeLock(a, on)),
        (Some("train"), 2) => number(1).map(C::TrainSkill),
        (Some("spec"), 2) => number(1).map(C::SpecializeSkill),
        (Some("untrain"), 2) => number(1).map(C::UntrainSkill),
        (Some("look"), 3) => appearance_slot(&w[1]).and_then(|slot| {
            // Only the hat can be none; every other slot holds a choice.
            if w[2].eq_ignore_ascii_case("none") {
                (slot == AppearanceSlot::HeadgearStyle).then_some(C::SetAppearance(slot, u32::MAX))
            } else {
                number(2).map(|n| C::SetAppearance(slot, n))
            }
        }),
        (Some("shade"), 3) => shade_slot(&w[1])
            .zip(w[2].parse::<f64>().ok())
            .map(|(slot, v)| C::SetShade(slot, v)),
        (Some("town"), 2) => w[1].parse::<i32>().ok().map(C::SelectStartArea),
        (Some("name"), n) if n >= 2 => Some(C::SetName(w[1..].join(" "))),
        (Some("slot"), 2) => number(1).map(C::SetSlot),
        (Some("finish"), 1) => Some(C::Finish {
            confirm_unspent_credits: false,
        }),
        (Some("finish"), 2) if w[1].eq_ignore_ascii_case("confirm") => Some(C::Finish {
            confirm_unspent_credits: true,
        }),
        (Some("ack"), 1) => Some(C::AcknowledgeRejection),
        (Some("random"), 1) => Some(C::RandomizeCharacter),
        (Some("random"), 2) => match w[1].to_ascii_lowercase().as_str() {
            "look" => Some(C::RandomizeAppearance),
            "clothes" => Some(C::RandomizeClothing),
            _ => None,
        },
        _ => None,
    };
    command.ok_or(USAGE)
}

fn appearance_slot(name: &str) -> Option<AppearanceSlot> {
    use AppearanceSlot as S;
    Some(match name.to_ascii_lowercase().as_str() {
        "eyes" => S::EyesStrip,
        "nose" => S::NoseStrip,
        "mouth" => S::MouthStrip,
        "hair" => S::HairStyle,
        "haircolor" => S::HairColor,
        "eyecolor" => S::EyeColor,
        "hat" => S::HeadgearStyle,
        "hatcolor" => S::HeadgearColor,
        "shirt" => S::ShirtStyle,
        "shirtcolor" => S::ShirtColor,
        "pants" => S::TrousersStyle,
        "pantscolor" => S::TrousersColor,
        "shoes" => S::FootwearStyle,
        "shoescolor" => S::FootwearColor,
        _ => return None,
    })
}

fn shade_slot(name: &str) -> Option<ShadeSlot> {
    use ShadeSlot as S;
    Some(match name.to_ascii_lowercase().as_str() {
        "skin" => S::Skin,
        "hair" => S::Hair,
        "hat" => S::Headgear,
        "shirt" => S::Shirt,
        "pants" => S::Trousers,
        "shoes" => S::Footwear,
        _ => return None,
    })
}

/// The console's help line.
pub const HELP: &str = "console: /objects, /time, /contracts, /journal, /window, /close, \
     /screenshot [file.png], /quit; items: /inv, /use <obj>, /useon <item> <target>, \
     /appraise <obj>, /pickup <obj>, /move <item> <container|me> [amount] [slot], \
     /merge <from> <to> [amount], /drop <item> [amount], /give <item> <target> [amount], \
     /wield <item> [slot bits], /unwield <item>, /inscribe <item> [text], /buy <item> [amount], /sell <item> [amount], \
     /salvage <tool> <item> [item ...], /mana <item>, /place <item> [onto <obj>] [amount], /ping, /link, \
     /dismiss <0xguid>, \
     /xtrade open|add|accept|decline|reset|close, /vendor [close], \
     /buylist [add <item> [n]|remove <item> [n]|clear|buy], /selllist [add <item>|remove <item>|clear|sell], \
     /corpses [metres], /confirm yes|no; options: /options, /option <name> on|off, \
     /saveoptions; gameplay: /select <obj>, /deselect, /target, /spells [filter], \
     /enchantments, /sheet, /vitae, /cast <spell> [target], /combat [peace|melee|missile|magic|status], \
     /attack [high|medium|low] [target], /power <0..1>, /stopattack, \
     /book [page <n>|write <text>|close], /fship [create|recruit|dismiss|quit|disband|leader|open|vitals], \
     /alleg [swear|break|kick|info|update], /myhouse [query], /titles [set <id>]; characters: /chars, \
     /enter [name|#index|0xguid], /logoff, /reconnect, /delete <name> then /delete yes, \
     /restore [name], /cancel, /nextlogin <name>|off, /create [heritage|gender|template|attr|lock|\
     train|spec|untrain|look|shade|town|name|slot|finish [confirm]|ack|random]; \
     movement: /walk|/run [back] [secs|<n>m], /strafe left|right [secs], /turn [left|right] <deg>, /turn to <heading>, \
     /jump [power], /stop, /autorun, /posture ready|sit|crouch|sleep, /where; \
     walks: /nav go to|stand on|follow|stop|status|debug, /motor walk|run|strafe|turn|jump|stop|status \
     (objects by 0xguid, \"quoted name\" or me); with several sessions \
     /sessions, /session <id> and @<id> <line>; anything else is typed into the chat box \
     (/say, /tell, /r, /f, /emote, /friends, /squelch, /log, @server commands, ...)";

#[cfg(test)]
mod tests {
    use super::*;
    use ac_runtime::communication::ChatKind;

    #[test]
    fn plain_text_and_chat_verbs_are_chat() {
        for raw in [
            "hello",
            "/say hi",
            "/tell Bob, hi",
            "/r back",
            "/f group",
            "@acehelp",
        ] {
            assert_eq!(
                parse(raw, &[]),
                ConsoleInput::Chat {
                    session: None,
                    line: raw
                },
                "{raw}"
            );
        }
        assert_eq!(parse("   ", &[]), ConsoleInput::Empty);
    }

    #[test]
    fn own_verbs_match_whole_and_ignore_case() {
        assert_eq!(parse("/QUIT", &[]), ConsoleInput::Quit { session: None });
        assert_eq!(parse("/time", &[]), ConsoleInput::Time { session: None });
        assert_eq!(
            parse("/objects bot-1", &["bot-1", "bot-2"]),
            ConsoleInput::Objects {
                session: Some("bot-1")
            }
        );
        assert_eq!(
            parse("/screenshot a.png", &[]),
            ConsoleInput::Screenshot("a.png")
        );
        // A longer verb is not an own verb.
        assert_eq!(
            parse("/quitter", &[]),
            ConsoleInput::Chat {
                session: None,
                line: "/quitter"
            }
        );
    }

    #[test]
    fn at_id_addresses_one_line_only_with_several_sessions() {
        let sessions = ["bot-1", "bot-2"];
        assert_eq!(
            parse("@bot-2 /tell Bob, hi", &sessions),
            ConsoleInput::Chat {
                session: Some("bot-2"),
                line: "/tell Bob, hi"
            }
        );
        assert_eq!(
            parse("@bot-2 /objects", &sessions),
            ConsoleInput::Objects {
                session: Some("bot-2")
            }
        );
        // Not a session: a server command.
        assert_eq!(
            parse("@tele holtburg", &sessions),
            ConsoleInput::Chat {
                session: None,
                line: "@tele holtburg"
            }
        );
        // One session: '@' is always the server's.
        assert_eq!(
            parse("@bot-1 hi", &["bot-1"]),
            ConsoleInput::Chat {
                session: None,
                line: "@bot-1 hi"
            }
        );
    }

    #[test]
    fn item_verbs_become_session_commands() {
        let command = |raw| match parse(raw, &[]) {
            ConsoleInput::Command { command, .. } => command,
            other => panic!("{raw}: {other:?}"),
        };
        assert_eq!(command("/inv"), ConsoleCommand::ListInventory);
        assert_eq!(
            command("/use"),
            ConsoleCommand::Item(ItemCommand::UseSelected)
        );
        assert_eq!(
            command("/appraise"),
            ConsoleCommand::Item(ItemCommand::ExamineSelected)
        );
        assert_eq!(
            command("/pickup"),
            ConsoleCommand::Item(ItemCommand::PickUpSelected)
        );
        assert_eq!(
            command("/pickup 0x7000000A main"),
            ConsoleCommand::Item(ItemCommand::PickUpToMainPack(ObjectRef::Guid(0x7000_000A)))
        );
        assert_eq!(
            command("/use 0x6000000A"),
            ConsoleCommand::Item(ItemCommand::Use(ObjectRef::Guid(0x6000_000A)))
        );
        assert_eq!(
            command("/move \"Iron Sword\" me 0 2"),
            ConsoleCommand::Item(ItemCommand::MoveToContainer {
                item: ObjectRef::Name("Iron Sword".into()),
                container: ObjectRef::Player,
                slot: 2,
                amount: 0,
                join_stack: false,
            })
        );
        assert_eq!(
            command("/give gem 0x50000002 3"),
            ConsoleCommand::Item(ItemCommand::Give {
                item: ObjectRef::Name("gem".into()),
                target: ObjectRef::Guid(0x5000_0002),
                amount: 3,
            })
        );
        assert_eq!(
            command("/xtrade accept"),
            ConsoleCommand::Item(ItemCommand::AcceptTrade)
        );
        assert_eq!(
            command("/inscribe sword \"Made by Horan\""),
            ConsoleCommand::Item(ItemCommand::Inscribe {
                item: ObjectRef::Name("sword".into()),
                text: "Made by Horan".into(),
            })
        );
        assert_eq!(
            command("/inscribe 0x60000004"),
            ConsoleCommand::Item(ItemCommand::Inscribe {
                item: ObjectRef::Guid(0x6000_0004),
                text: String::new(),
            })
        );
        assert_eq!(
            command("/option listentogeneralchat off"),
            ConsoleCommand::SetOption {
                option: 0x23,
                value: false
            }
        );
        assert_eq!(
            command("/salvage ust 0x60000021 \"Iron Sword\""),
            ConsoleCommand::Item(ItemCommand::Salvage {
                tool: ObjectRef::Name("ust".into()),
                items: vec![
                    ObjectRef::Guid(0x6000_0021),
                    ObjectRef::Name("Iron Sword".into())
                ],
            })
        );
        assert_eq!(
            command("/mana sword"),
            ConsoleCommand::Item(ItemCommand::QueryMana(ObjectRef::Name("sword".into())))
        );
        assert_eq!(
            command("/place sword onto 0x50000002"),
            ConsoleCommand::Item(ItemCommand::Place {
                item: ObjectRef::Name("sword".into()),
                target: Some(ObjectRef::Guid(0x5000_0002)),
                amount: 0,
            })
        );
        assert_eq!(
            command("/place gem 3"),
            ConsoleCommand::Item(ItemCommand::Place {
                item: ObjectRef::Name("gem".into()),
                target: None,
                amount: 3,
            })
        );
        assert!(matches!(
            parse("/place gem onto", &[]),
            ConsoleInput::Usage(_)
        ));
        assert_eq!(
            command("/dismiss 0x70000001"),
            ConsoleCommand::DismissGhost(0x7000_0001)
        );
        assert!(matches!(
            parse("/dismiss drudge", &[]),
            ConsoleInput::Usage(_)
        ));
        assert_eq!(command("/ping"), ConsoleCommand::Ping);
        assert_eq!(command("/link"), ConsoleCommand::LinkStatus);
        for raw in ["/salvage ust", "/mana"] {
            assert!(matches!(parse(raw, &[]), ConsoleInput::Usage(_)), "{raw}");
        }
    }

    #[test]
    fn vendor_death_and_corpse_verbs_become_session_commands() {
        let command = |raw| match parse(raw, &[]) {
            ConsoleInput::Command { command, .. } => command,
            other => panic!("{raw}: {other:?}"),
        };
        let vendor = |c| ConsoleCommand::Item(ItemCommand::Vendor(c));
        assert_eq!(command("/vendor"), vendor(VendorCommand::Show));
        assert_eq!(command("/vendor close"), vendor(VendorCommand::Close));
        assert_eq!(
            command("/buylist add \"Lead Scarab\" 5"),
            vendor(VendorCommand::AddToBuyList {
                item: ObjectRef::Name("Lead Scarab".into()),
                amount: 5
            })
        );
        assert_eq!(
            command("/buylist remove bread"),
            vendor(VendorCommand::RemoveFromBuyList {
                item: ObjectRef::Name("bread".into()),
                amount: -1
            })
        );
        assert_eq!(command("/buylist buy"), vendor(VendorCommand::BuyAll));
        assert_eq!(
            command("/selllist add 0x80000604"),
            vendor(VendorCommand::AddToSellList(ObjectRef::Guid(0x8000_0604)))
        );
        assert_eq!(command("/selllist sell"), vendor(VendorCommand::SellAll));
        assert_eq!(
            command("/confirm yes"),
            ConsoleCommand::AnswerConfirmation(true)
        );
        assert_eq!(
            command("/confirm no"),
            ConsoleCommand::AnswerConfirmation(false)
        );
        assert_eq!(command("/corpses"), ConsoleCommand::ListCorpses(100.0));
        assert_eq!(command("/corpses 30"), ConsoleCommand::ListCorpses(30.0));
        assert_eq!(
            command("/vitae"),
            ConsoleCommand::Gameplay(GameplayCommand::ShowVitae)
        );
        // Retail's own `/die`, `/corpse` and `/fillcomps` stay chat-box
        // commands.
        for raw in ["/die", "/corpse", "/fillcomps scarabs 500"] {
            assert!(
                matches!(parse(raw, &[]), ConsoleInput::Chat { .. }),
                "{raw}"
            );
        }
    }

    #[test]
    fn item_verbs_used_wrongly_answer_with_their_usage() {
        for raw in [
            "/use a b",
            "/useon sword",
            "/xtrade dance",
            "/option nosuch on",
            "/vendor open",
            "/buylist add",
            "/selllist sell now",
            "/confirm maybe",
            "/corpses far",
            "/inscribe",
        ] {
            assert!(matches!(parse(raw, &[]), ConsoleInput::Usage(_)), "{raw}");
        }
        // Chat verbs stay chat.
        assert!(matches!(
            parse("/trade wts sword", &[]),
            ConsoleInput::Chat { .. }
        ));
    }

    #[test]
    fn chat_lines_print_with_their_tag_and_session() {
        let line = ChatLine {
            sequence: 1,
            kind: ChatKind::LocalSpeech,
            log_text_type: 2,
            combat_kind: None,
            received_unix_ms: 0,
            text: "Bob says, \"hi\"".into(),
            channel_name: String::new(),
            sender: "Bob".into(),
            sender_guid: 0x5000_0002,
            words: String::new(),
            display_text: String::new(),
            channel_id: 0,
            spans: None,
        };
        assert_eq!(chat_line(&line, None), "[say] Bob says, \"hi\"");
        assert_eq!(
            chat_line(&line, Some("bot-1")),
            "[bot-1] [say] Bob says, \"hi\""
        );
        assert_eq!(
            interface_text("You can't jump while in the air", None),
            "[client] You can't jump while in the air"
        );
        assert_eq!(notice("entered world", Some("a")), "-- [a] entered world");
    }

    #[test]
    fn a_line_is_marked_with_its_kind_only_when_the_launcher_asks() {
        let line = "[system] Welcome\n  powered by ACEmulator";
        assert_eq!(marked(Some(ConsoleLineKind::Chat), line, false), line);
        assert_eq!(
            marked(Some(ConsoleLineKind::Chat), line, true),
            "\u{1e}c[system] Welcome\n\u{1e}c  powered by ACEmulator"
        );
        // The client's log is never marked.
        assert_eq!(marked(None, "-- portal: reveal", true), "-- portal: reveal");
    }

    fn gameplay(event: GameplayEvent) -> HostEvent {
        HostEvent::Gameplay(Box::new(event))
    }

    /// An appraisal answer; `asked` when it answers the player's own
    /// question (the examination window opens on it).
    fn appraisal(asked: bool) -> HostEvent {
        HostEvent::Appraisal(Box::new(ac_runtime::inventory::AppraisalView {
            name: Some("Sword".into()),
            response: ac_proto::appraise::IdentifyObjectResponse {
                guid: 7,
                success: true,
                ..Default::default()
            },
            present_in_ui: asked,
            opens_window: asked,
        }))
    }

    /// The events a typed command, a plugin or a bot policy can cause alike.
    fn answers() -> Vec<HostEvent> {
        use ac_runtime::movement::MovementEvent;
        vec![
            HostEvent::GameTime(None),
            HostEvent::Objects(Vec::new()),
            HostEvent::Corpses(Vec::new()),
            HostEvent::CommandRefused("no such object".into()),
            gameplay(GameplayEvent::Refused("no spell".into())),
            HostEvent::Movement(MovementEvent::Status(None)),
            HostEvent::Movement(MovementEvent::Refused("not placed".into())),
            gameplay(GameplayEvent::Selection {
                selected: Some(7),
                name: None,
            }),
            appraisal(true),
        ]
    }

    #[test]
    fn chat_and_the_status_box_are_always_what_a_player_sees() {
        let window = AnswerWindow::default();
        let now = std::time::Instant::now();
        let chat = ChatLine {
            sequence: 1,
            kind: ChatKind::LocalSpeech,
            log_text_type: 2,
            combat_kind: None,
            received_unix_ms: 0,
            text: "Bob says, \"hi\"".into(),
            channel_name: String::new(),
            sender: "Bob".into(),
            sender_guid: 0x5000_0002,
            words: String::new(),
            display_text: String::new(),
            channel_id: 0,
            spans: None,
        };
        assert_eq!(
            event_kind(&HostEvent::Chat(chat), &window, now),
            Some(ConsoleLineKind::Chat)
        );
        assert_eq!(
            event_kind(&HostEvent::InterfaceText("You fail".into()), &window, now),
            Some(ConsoleLineKind::Status)
        );
        // What only the client's log keeps, even right after a command.
        window.open(now);
        for log in [
            HostEvent::Logout(ac_runtime::world::transit::LogoutStage::Requested),
            gameplay(GameplayEvent::Health {
                guid: 1,
                percent: 0.5,
            }),
            gameplay(GameplayEvent::CreatureDied(1)),
        ] {
            assert_eq!(event_kind(&log, &window, now), None, "{log:?}");
        }
    }

    #[test]
    fn answers_a_plugin_or_a_policy_caused_are_the_clients_log() {
        // No command was typed on this console: a looting bot's identify,
        // a plugin's refused command, a policy's movement status.
        let window = AnswerWindow::default();
        let now = std::time::Instant::now();
        for event in answers() {
            assert_eq!(event_kind(&event, &window, now), None, "{event:?}");
        }
    }

    #[test]
    fn a_typed_commands_answers_are_shown_until_the_window_closes() {
        let window = AnswerWindow::default();
        let typed = std::time::Instant::now();
        window.open(typed);
        let soon = typed + ANSWER_WINDOW / 2;
        for event in answers() {
            assert_eq!(
                event_kind(&event, &window, soon),
                Some(ConsoleLineKind::Reply),
                "{event:?}"
            );
        }
        // A plugin's quiet identify is not the player's question.
        assert_eq!(event_kind(&appraisal(false), &window, soon), None);
        let late = typed + ANSWER_WINDOW;
        for event in answers() {
            assert_eq!(event_kind(&event, &window, late), None, "{event:?}");
        }
        // Another command opens one window again, from its own time; many
        // commands keep one window, not a queue.
        for _ in 0..3 {
            window.open(late);
        }
        assert!(window.is_open(late + ANSWER_WINDOW / 2));
        assert!(!window.is_open(late + ANSWER_WINDOW));
    }

    #[test]
    fn the_console_prints_a_line_with_its_tags_hidden() {
        // OpenAC chat tag fix: the console prints the text
        // the chat window shows (`RuntimeChatFeedTests.AServerLinesTagIsHiddenAndItsTellNameIsTagged`).
        let mut log = ac_core::chat::ChatLog::new();
        log.on_system_message(0, r"<Tell:IIDString:1:Bob>Bob<\Tell> has logged on.", 0);
        log.on_local_speech(
            0,
            "You",
            r"ask <Tell:IIDString:7:Ann>Ann<\Tell>",
            0,
            false,
            2,
        );
        let printed: Vec<String> = log
            .entries()
            .map(|e| chat_line(&ChatLine::from_entry(e, false, 0), None))
            .collect();
        assert_eq!(
            printed,
            ["[msg] Bob has logged on.", "[say] You say, \"ask Ann\""]
        );
    }

    #[test]
    fn the_console_shows_what_the_main_window_shows_in_its_colour() {
        // OpenAC HeadlessConsoleChatParityTests.ATextClassTheMainWindowFiltersOutIsShownOnNeitherFrontEnd
        // (the main window's default filter) and HeadlessConsoleTests.
        // AChatLineIsWrittenInTheColourTheChatWindowShowsItIn.
        let line = |log_text_type| ChatLine {
            sequence: 1,
            kind: ChatKind::LocalSpeech,
            log_text_type,
            combat_kind: None,
            received_unix_ms: 0,
            text: "Bob says, \"hi\"".into(),
            channel_name: String::new(),
            sender: "Bob".into(),
            sender_guid: 0x5000_0002,
            words: String::new(),
            display_text: String::new(),
            channel_id: 0,
            spans: None,
        };
        // A tell (3) shows; plain without colour.
        let tell = console_chat_line(&line(3), None, false).unwrap();
        assert!(!tell.contains('\u{1b}'));
        // With colour, in the window's tell colour, then a reset.
        let coloured = console_chat_line(&line(3), None, true).unwrap();
        let expected = chat_color_escape(3).unwrap();
        assert!(coloured.starts_with(&expected), "{coloured:?}");
        assert!(coloured.ends_with(ANSI_RESET));
        // Client-local text (0x1A) and Society (0x20 up) are not the main
        // window's.
        assert_eq!(console_chat_line(&line(0x1A), None, false), None);
        assert_eq!(console_chat_line(&line(0x20), None, false), None);
    }

    #[test]
    fn gameplay_verbs_become_session_commands() {
        use ac_runtime::gameplay::{
            AllegianceCommand as A, AttackHeight, BookCommand as B, CombatMode,
            FellowshipCommand as F, GameplayCommand as G,
        };
        let command = |raw: &str| match parse(raw, &[]) {
            ConsoleInput::Command {
                command: ConsoleCommand::Gameplay(command),
                ..
            } => command,
            other => panic!("{raw}: {other:?}"),
        };
        assert_eq!(
            command("/cast \"Strength Self I\" me"),
            G::Cast {
                spell: "Strength Self I".into(),
                target: Some(ObjectRef::Player),
            }
        );
        assert_eq!(command("/combat"), G::ToggleCombat);
        assert_eq!(
            command("/combat magic"),
            G::SetCombatMode(CombatMode::MAGIC)
        );
        assert_eq!(command("/combat status"), G::ShowCombat);
        assert_eq!(
            command("/attack low Drudge"),
            G::Attack {
                height: AttackHeight::LOW,
                target: Some(ObjectRef::Name("Drudge".into())),
            }
        );
        assert_eq!(
            command("/attack"),
            G::Attack {
                height: AttackHeight::MEDIUM,
                target: None,
            }
        );
        assert_eq!(command("/power 0.75"), G::SetAttackPower(0.75));
        assert_eq!(
            command("/spells strength"),
            G::ListSpells(Some("strength".into()))
        );
        assert_eq!(command("/book page 2"), G::Book(B::Turn(2)));
        assert_eq!(
            command("/book write Hello there"),
            G::Book(B::Write("Hello there".into()))
        );
        assert_eq!(
            command("/fship create Friends share"),
            G::Fellowship(F::Create {
                name: "Friends".into(),
                share_xp: true,
            })
        );
        assert_eq!(
            command("/fship disband"),
            G::Fellowship(F::Quit { disband: true })
        );
        assert_eq!(command("/fship"), G::Fellowship(F::Show));
        assert_eq!(command("/titles"), G::ShowTitles);
        {
            use ac_runtime::gameplay::selection_cycle::{
                SelectionCycleDirection as D, SelectionCycleKind as K,
            };
            let cycle = |kind, direction| G::CycleSelection { kind, direction };
            assert_eq!(command("/target"), G::SelectNearestMonster);
            assert_eq!(command("/target player"), cycle(K::Player, D::Closest));
            assert_eq!(command("/target next"), cycle(K::Player, D::Next));
            assert_eq!(command("/target prev"), cycle(K::Player, D::Previous));
            assert_eq!(command("/target back"), G::SelectPrevious);
            assert_eq!(command("/target monster next"), cycle(K::Monster, D::Next));
            assert_eq!(command("/target item"), cycle(K::Item, D::Closest));
            assert_eq!(
                command("/target compass previous"),
                cycle(K::CompassItem, D::Previous)
            );
            assert_eq!(
                command("/target corpse next"),
                cycle(K::UnopenedCorpse, D::Next)
            );
            assert_eq!(
                command("/target corpse use"),
                G::UseUnopenedCorpse(D::Closest)
            );
            assert_eq!(command("/target self"), G::SelectSelf);
            assert_eq!(command("/target attacker"), G::SelectLastAttacker);
            assert_eq!(
                command("/target fellow prev"),
                G::CycleFellow { previous: true }
            );
        }
        assert_eq!(command("/titles set 13"), G::SetTitle(13));
        assert_eq!(
            command("/alleg info +Acdream"),
            G::Allegiance(A::Info("+Acdream".into()))
        );
        assert_eq!(command("/myhouse query"), G::QueryHouse);
        for raw in [
            "/cast",
            "/combat dance",
            "/power 2",
            "/book page x",
            "/fship open maybe",
        ] {
            assert!(matches!(parse(raw, &[]), ConsoleInput::Usage(_)), "{raw}");
        }
        // Retail's own verbs stay chat.
        for raw in ["/house", "/allegiance info", "/f hello", "/fellowship hi"] {
            assert!(
                matches!(parse(raw, &[]), ConsoleInput::Chat { .. }),
                "{raw}"
            );
        }
    }

    #[test]
    fn character_and_lifecycle_verbs_become_lifecycle_commands() {
        use LifecycleCommand as L;
        let command = |raw: &str| match parse(raw, &[]) {
            ConsoleInput::Command {
                command: ConsoleCommand::Lifecycle(c),
                ..
            } => c,
            other => panic!("{raw}: {other:?}"),
        };
        assert_eq!(command("/chars"), L::ShowSelection);
        assert_eq!(command("/logoff"), L::LogOff);
        assert_eq!(command("/reconnect"), L::Reconnect);
        // A session is addressed with `@<id>` (OpenAC's console addresses
        // sessions only that way); a trailing word is refused, not ignored.
        for raw in ["/logoff h", "/chars h", "/reconnect h", "/cancel h"] {
            assert!(
                matches!(parse(raw, &["a", "h"]), ConsoleInput::Usage(u) if u.contains("@<id>")),
                "{raw}"
            );
        }
        assert!(matches!(
            parse("@h /logoff", &["a", "h"]),
            ConsoleInput::Command {
                session: Some("h"),
                command: ConsoleCommand::Lifecycle(L::LogOff),
            }
        ));
        assert_eq!(command("/enter"), L::Enter(None));
        assert_eq!(
            command("/enter +Horan"),
            L::Enter(Some(CharacterSelector::Name("+Horan".into())))
        );
        assert_eq!(
            command("/delete #2"),
            L::RequestDelete(Some(CharacterSelector::Index(2)))
        );
        assert_eq!(command("/delete yes"), L::ConfirmDelete);
        assert_eq!(
            command("/restore 0x50000001"),
            L::Restore(Some(CharacterSelector::Id(0x5000_0001)))
        );
        assert_eq!(command("/nextlogin off"), L::SetNextLogin(None));
        assert_eq!(command("/create"), L::Create(CreationCommand::Show));
        assert_eq!(
            command("/create gender female"),
            L::Create(CreationCommand::SelectGender(2))
        );
        assert_eq!(
            command("/create attr coord 50"),
            L::Create(CreationCommand::SetAttribute(
                ChargenAttribute::Coordination,
                50
            ))
        );
        assert_eq!(
            command("/create name Rust Test"),
            L::Create(CreationCommand::SetName("Rust Test".into()))
        );
        assert_eq!(
            command("/create look hat none"),
            L::Create(CreationCommand::SetAppearance(
                AppearanceSlot::HeadgearStyle,
                u32::MAX
            ))
        );
        assert_eq!(
            command("/create finish confirm"),
            L::Create(CreationCommand::Finish {
                confirm_unspent_credits: true
            })
        );
        for raw in [
            "/delete",
            "/nextlogin",
            "/create attr luck 5",
            "/create look ears 1",
            "/create look hair none",
        ] {
            assert!(matches!(parse(raw, &[]), ConsoleInput::Usage(_)), "{raw}");
        }
    }
}
