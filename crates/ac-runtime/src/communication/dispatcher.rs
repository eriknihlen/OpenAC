//! What each client command does: a request to the server, a change to the
//! chat state, or a line for the player.

use ac_core::items::PublicWeenieFlags;
use ac_core::social::{FriendsState, SquelchDatabase};

use super::administration::{self, AdministrationHost, AdministrationRequest};
use super::catalog::{self, ClientCommandId};
use super::input::resolve_channel_tag;
use super::log_file::ChatLogResult;
use crate::character::option_id;

/// The most friends the list holds.
pub const MAX_FRIENDS: usize = 50;

/// A request a client command sends to the server.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ClientRequest {
    /// Recall to the last lifestone.
    TeleportToLifestone,
    /// Recall to the Marketplace.
    TeleportToMarketplace,
    /// Recall to the PK arena.
    TeleportToPkArena,
    /// Recall to the PK-lite arena.
    TeleportToPkLiteArena,
    /// Recall to the player's house.
    TeleportToHouse,
    /// Recall to the allegiance mansion.
    TeleportToMansion,
    /// Recall to the allegiance bindstone.
    RecallAllegianceHometown,
    /// Become PK-lite.
    EnterPkLite,
    /// Ask how long the character has played.
    QueryAge,
    /// Ask when the character was created.
    QueryBirth,
    /// Turn away-from-keyboard on or off.
    SetAfkMode(bool),
    /// Set the away message.
    SetAfkMessage(String),
    /// Show the consent list.
    DisplayConsent,
    /// Clear the consent list.
    ClearConsent,
    /// Remove someone from the consent list.
    RemoveConsent(String),
    /// A text emote.
    Emote(String),
    /// Add a friend by name.
    AddFriend(String),
    /// Remove a friend by character id.
    RemoveFriend(u32),
    /// Clear the friends list.
    ClearFriends,
    /// The legacy friends-list request.
    LegacyFriendsList,
    /// Squelch or unsquelch a character's messages of one type.
    CharacterSquelch {
        /// Squelch (true) or unsquelch.
        add: bool,
        /// The character id (0 when given by name).
        character_id: u32,
        /// The character's name.
        name: String,
        /// The message type.
        message_type: u32,
    },
    /// Squelch or unsquelch an account by character name.
    AccountSquelch {
        /// Squelch (true) or unsquelch.
        add: bool,
        /// A character on the account.
        name: String,
    },
    /// Squelch or unsquelch one message type from everyone.
    GlobalSquelch {
        /// Squelch (true) or unsquelch.
        add: bool,
        /// The message type.
        message_type: u32,
    },
    /// Ask for the channel index.
    IndexChannels,
    /// Ask who listens on a channel.
    ListChannel(u32),
    /// Join a channel.
    OnChannel(u32),
    /// Leave a channel.
    OffChannel(u32),
    /// Let a character loot the player's corpse.
    AddPlayerPermission(String),
    /// Stop letting a character loot the player's corpse.
    RemovePlayerPermission(String),
    /// Ask which houses of a type are for sale.
    ListAvailableHouses(u32),
    /// Ask for a player's allegiance information (the answer is chat).
    AllegianceInfo(String),
    /// An allegiance or house administration request.
    Administration(AdministrationRequest),
    /// Kill the character (after the player confirmed).
    Suicide,
}

/// A question the player answers yes or no: one the client asks before it
/// acts, or one the server asks (swear allegiance, join a fellowship, a
/// crafting or augmentation step, ...).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ClientConfirmation {
    /// `/die`: kill the character.
    Suicide,
    /// `/house abandon`, first question.
    AbandonHouse,
    /// `/house abandon`, asked again after a yes; a second yes abandons.
    AbandonHouseCertain,
    /// Use an item only after a yes (a player-killer altar, a volatile
    /// rare): the item policy's confirmation.
    ItemUse {
        /// Which question.
        kind: ItemUseConfirmation,
        /// The item.
        object_id: u32,
    },
    /// Charge an empty mana stone from an item, destroying the item, only
    /// after a yes.
    ManaStoneDrain {
        /// The mana stone.
        stone: u32,
        /// The item it drains.
        target: u32,
        /// The question, naming the item.
        message: String,
    },
    /// The server's question; the answer echoes its kind and id.
    Server {
        /// Its kind (`ac_proto::social::confirmation_type`).
        confirmation_type: u32,
        /// The server's id for it.
        context_id: u32,
        /// Its text as sent (for some kinds a name the dialog words into
        /// its own question).
        message: String,
    },
}

impl ClientConfirmation {
    /// The question as the game asks it (the server's text as sent).
    #[must_use]
    pub fn text(&self) -> &str {
        match self {
            Self::Suicide => {
                "Do you really want to kill your character? You may drop items and accrue a vitae penalty."
            }
            Self::AbandonHouse => {
                "Do you really want to abandon your house? Any items in the house (on hooks or in storage) will stay with the house, and you will lose access to them."
            }
            Self::AbandonHouseCertain => {
                "Are you absolutely certain you wish to abandon your house? Click yes only if you are sure!"
            }
            Self::ItemUse { kind, .. } => kind.text(),
            Self::ManaStoneDrain { message, .. } | Self::Server { message, .. } => message,
        }
    }
}

/// The item uses that are asked first.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ItemUseConfirmation {
    /// A player-killer altar.
    PlayerKillerSwitch,
    /// A non-player-killer altar.
    NonPlayerKillerSwitch,
    /// A volatile rare.
    VolatileRare,
}

impl ItemUseConfirmation {
    /// The question.
    #[must_use]
    pub fn text(self) -> &'static str {
        match self {
            Self::PlayerKillerSwitch => {
                "Using this altar will make you a player killer, able to attack or be attacked by other player killers. Are you sure you want to do this?"
            }
            Self::NonPlayerKillerSwitch => {
                "Using this altar will make you a non-player killer, unable to attack or be attacked by other player killers. Are you sure you want to do this?"
            }
            Self::VolatileRare => "Are you sure you want to use this rare item?",
        }
    }
}

/// Work a client command hands to another owner of the session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LocalRequest {
    /// `/fillcomps clear`: every desired component amount goes to zero.
    ClearDesiredComponents,
    /// `/fillcomps`: fill the open vendor's buy list from the component
    /// book.
    FillComponents {
        /// One component category, or every one
        /// ([`ac_core::items::component_fill::ANY_CATEGORY`]).
        category: u32,
        /// The spending ceiling; 0 for none.
        maximum_price: u32,
    },
    /// The player said yes to using this item (an item-use question).
    ConfirmedUse(u32),
    /// The player said yes to charging this mana stone from this item.
    ConfirmedManaStoneDrain {
        /// The mana stone.
        stone: u32,
        /// The item it drains.
        target: u32,
    },
}

/// What the dispatcher reads and changes outside itself.
pub trait ClientCommandHost {
    /// Send a request to the server.
    fn send(&mut self, request: ClientRequest);
    /// A line in the transcript.
    fn show_system_message(&mut self, text: &str);
    /// Client-local text (the status overlay, not the transcript).
    fn show_client_local(&mut self, text: &str);
    /// The text of a failure code, routed by its text class.
    fn show_weenie_error(&mut self, code: u32);
    /// The player's public description bits, when described.
    fn player_public_weenie_bitfield(&self) -> Option<u32>;
    /// Whether the player is away from the keyboard.
    fn is_away(&self) -> bool;
    /// Empty the transcript.
    fn clear_chat(&mut self, all: bool);
    /// Open or close the `@log` file.
    fn set_chat_log_file(&mut self, name: &str) -> ChatLogResult;
    /// The friends list.
    fn friends(&self) -> &FriendsState;
    /// Empty the local friends list.
    fn clear_friends_locally(&mut self);
    /// The squelch database.
    fn squelch(&self) -> &SquelchDatabase;
    /// Who last sent the player a tell.
    fn last_teller(&self) -> Option<&str>;
    /// Whether chat rooms are on.
    fn is_using_turbine_chat(&self) -> bool;
    /// This client's version text.
    fn client_version(&self) -> &str;
    /// One character option's value.
    fn character_option(&self, option_id: u32) -> bool;
    /// Change one character option the way the options panel does (saved
    /// at once or with the next block save, as the option says).
    fn set_character_option(&mut self, option_id: u32, value: bool);
    /// The cell of the player's last corpse outside, when the server told.
    fn last_outside_corpse_cell(&self) -> Option<u32>;
    /// Where the character's body stands: cell, origin, orientation
    /// `w x y z`.
    fn current_position(&self) -> Option<(u32, [f32; 3], [f32; 4])>;
    /// Ask the player a question; the answer comes back later.
    fn ask_confirmation(&mut self, confirmation: ClientConfirmation);
    /// Whether a vendor's shop is open.
    fn has_open_vendor(&self) -> bool;
    /// Hand work to another owner.
    fn request(&mut self, request: LocalRequest);
    /// Something only an attached window's interface does; without a
    /// window the player reads [`NEEDS_A_WINDOW`].
    fn window(&mut self, request: WindowRequest);
}

/// What a command answers when it acts on a window's interface and the
/// session has none attached.
pub const NEEDS_A_WINDOW: &str = "That command needs a window, and this client does not have one.";

/// A client command that acts on the attached window's interface.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WindowRequest {
    /// `/framerate`: show or hide the frame-rate display.
    ToggleFrameRate,
    /// `/saveui [name]`: save the window layout under a name ("" for the
    /// default profile).
    SaveUi(String),
    /// `/loadui [name]`: restore a named window layout.
    LoadUi(String),
    /// `/saveautoui`: save the character's own window layout.
    SaveAutoUi,
    /// `/loadautoui`: restore the character's own window layout.
    LoadAutoUi,
    /// `/render radius <n>`: the landscape draw distance, 5..=25
    /// landblocks (the display setting, saved).
    SetLandscapeRadius(i32),
    /// `/render fov <n>`: the field of view, 10..=160 degrees (the display
    /// setting, saved).
    SetFieldOfView(i32),
    /// A plugin puts text on the system clipboard.
    SetClipboard(String),
    /// A plugin minimizes the window.
    Minimize,
    /// A plugin restores the minimized window.
    Restore,
    /// The Escape key found nothing to stop, cancel or deselect: open the
    /// options window on its gameplay page.
    GameplayOptionsPage,
}

/// Run one client command.
pub fn execute(command: ClientCommandId, arguments: &str, host: &mut impl ClientCommandHost) {
    use ClientCommandId as C;
    match command {
        C::LifestoneRecall => host.send(ClientRequest::TeleportToLifestone),
        C::MarketplaceRecall => host.send(ClientRequest::TeleportToMarketplace),
        C::PkArenaRecall => {
            if has_player_flag(host, PublicWeenieFlags::PLAYER_KILLER) == Some(false) {
                host.show_weenie_error(0x055F);
            } else {
                host.send(ClientRequest::TeleportToPkArena);
            }
        }
        C::PkLiteArenaRecall => {
            if has_player_flag(host, PublicWeenieFlags::PLAYER_KILLER_LITE) == Some(false) {
                host.show_weenie_error(0x0560);
            } else {
                host.send(ClientRequest::TeleportToPkLiteArena);
            }
        }
        C::EnterPkLite => {
            if has_player_flag(host, PublicWeenieFlags::PLAYER_KILLER) == Some(true)
                || has_player_flag(host, PublicWeenieFlags::PLAYER_KILLER_LITE) == Some(true)
            {
                host.show_weenie_error(0x0507);
            } else {
                host.send(ClientRequest::EnterPkLite);
            }
        }
        C::HouseRecall => host.send(ClientRequest::TeleportToHouse),
        C::MansionRecall => host.send(ClientRequest::TeleportToMansion),
        C::AllegianceHometown => host.send(ClientRequest::RecallAllegianceHometown),
        C::QueryAge => host.send(ClientRequest::QueryAge),
        C::QueryBirth => host.send(ClientRequest::QueryBirth),
        C::ShowVersion => {
            let text = if host.is_using_turbine_chat() {
                format!(
                    "Client version {}\nUsing Turbine Chat.",
                    host.client_version()
                )
            } else {
                format!("Client version {}", host.client_version())
            };
            host.show_system_message(&text);
        }
        C::ClearChat => host.clear_chat(first_argument(arguments).eq_ignore_ascii_case("all")),
        C::ChatLogFile => chat_log_file(arguments, host),
        C::Away => away(arguments, host),
        C::Consent => consent(arguments, host),
        C::Emote => {
            if !arguments.trim().is_empty() {
                host.send(ClientRequest::Emote(arguments.trim().to_owned()));
            }
        }
        C::ListEmotes => host.show_system_message(STANDARD_EMOTES),
        C::Friends => friends(arguments, host),
        C::FriendsAdd => add_friend(arguments, host),
        C::FriendsRemove => remove_friend(arguments, host),
        C::Squelch => squelch(arguments, true, host),
        C::Unsquelch => squelch(arguments, false, host),
        C::Filter => global_filter(arguments, true, host),
        C::Unfilter => global_filter(arguments, false, host),
        C::ListMessageTypes => {
            let names: Vec<&str> = MESSAGE_TYPES.iter().map(|(_, name)| *name).collect();
            host.show_system_message(&format!(
                "Squelch channels are as follows:\n  {}",
                names.join(", ")
            ));
        }
        C::Endurance => host.show_system_message(ENDURANCE_TEXT),
        C::Speaker => host.show_system_message(
            "This command is no longer in use, please see @allegiance officer.",
        ),
        C::ChatToggle => host.send(ClientRequest::GlobalSquelch {
            add: arguments.eq_ignore_ascii_case("off"),
            message_type: 2,
        }),
        C::NoTellToggle => host.send(ClientRequest::GlobalSquelch {
            add: arguments.eq_ignore_ascii_case("on"),
            message_type: 3,
        }),
        C::Permit => {
            let parts: Vec<&str> = arguments.split_whitespace().collect();
            let name = parts.get(1..).unwrap_or_default().join(" ");
            if parts.first().is_some_and(|p| p.eq_ignore_ascii_case("add")) {
                host.send(ClientRequest::AddPlayerPermission(name));
            } else {
                host.send(ClientRequest::RemovePlayerPermission(name));
            }
        }
        C::HouseAvailableList => {
            if let Some(house_type) = catalog::house_type(arguments) {
                host.send(ClientRequest::ListAvailableHouses(house_type));
            }
        }
        C::IndexChannels => host.send(ClientRequest::IndexChannels),
        C::ListChannel | C::OnChannel | C::OffChannel => {
            match resolve_channel_tag(arguments.trim()) {
                Some(id) => host.send(match command {
                    C::ListChannel => ClientRequest::ListChannel(id),
                    C::OnChannel => ClientRequest::OnChannel(id),
                    _ => ClientRequest::OffChannel(id),
                }),
                None => host.show_weenie_error(0x0422),
            }
        }
        C::TogglePersistentDaylight => {
            let enabled = !host.character_option(option_id::PERSISTENT_AT_DAY);
            host.set_character_option(option_id::PERSISTENT_AT_DAY, enabled);
            host.show_system_message(if enabled {
                "Let there be light!"
            } else {
                "Normality has been restored."
            });
        }
        C::ToggleUiLock => {
            // The lock is a character option either way; only showing it
            // needs a window.
            let locked = !host.character_option(option_id::LOCK_UI);
            host.set_character_option(option_id::LOCK_UI, locked);
        }
        C::JoinChannel | C::LeaveChannel => {
            if let Some(option) = catalog::join_leave_option(arguments) {
                host.set_character_option(option, command == C::JoinChannel);
            }
        }
        C::ShowLastCorpseLocation => {
            let coordinates = host
                .last_outside_corpse_cell()
                .and_then(crate::gameplay::house::radar_coordinates);
            host.show_system_message(&match coordinates {
                Some((north_south, east_west)) => format!(
                    "The last time you died outside, your corpse was located at ({north_south},{east_west})."
                ),
                None => {
                    "We're sorry, but we have no record of your last outside corpse location."
                        .to_owned()
                }
            });
        }
        C::Die => host.ask_confirmation(ClientConfirmation::Suicide),
        C::FillComponents => fill_components(arguments, host),
        C::ShowLocation => {
            let text = match host.current_position() {
                Some((cell, origin, orientation)) if cell != 0 => format!(
                    "Your location is: {}",
                    ac_core::text::format_retail_position(cell, origin, orientation)
                ),
                _ => "Not in valid cell!".to_owned(),
            };
            host.show_system_message(&text);
        }
        C::ToggleFrameRate => host.window(WindowRequest::ToggleFrameRate),
        C::SaveUi => ui_profile(arguments, true, host),
        C::LoadUi => ui_profile(arguments, false, host),
        C::SaveAutoUi => {
            if require_no_arguments(arguments, "/saveautoui", host) {
                host.window(WindowRequest::SaveAutoUi);
            }
        }
        C::LoadAutoUi => {
            if require_no_arguments(arguments, "/loadautoui", host) {
                host.window(WindowRequest::LoadAutoUi);
            }
        }
        C::RenderOption => render_option(arguments, host),
        // The popup chat window's title is not kept by either client: the
        // command is accepted with any arguments and does nothing, silently.
        C::SetChatTitle => {}
        C::HouseAbandon => host.ask_confirmation(ClientConfirmation::AbandonHouse),
        C::AllegianceInfo
        | C::AllegianceBoot
        | C::AllegianceBan
        | C::AllegianceChat
        | C::AllegianceBroadcast
        | C::AllegianceOfficer
        | C::AllegianceOfficerTitle
        | C::AllegianceName
        | C::AllegianceLock
        | C::AllegianceHouse
        | C::AllegianceMotd
        | C::AllegianceUnrecognizedSubcommand
        | C::HouseOpenStatus
        | C::HouseStorage
        | C::HouseBoot
        | C::HouseBootAll
        | C::HouseGuests
        | C::HouseHooks
        | C::HouseUnrecognizedSubcommand => {
            administration::execute(command, arguments, &mut Administration(host));
        }
    }
}

/// The dispatcher's host as the administration commands' host.
struct Administration<'h, H>(&'h mut H);

impl<H: ClientCommandHost> AdministrationHost for Administration<'_, H> {
    fn send(&mut self, request: AdministrationRequest) {
        self.0.send(ClientRequest::Administration(request));
    }
    fn show_system_message(&mut self, text: &str) {
        self.0.show_system_message(text);
    }
    fn show_client_local(&mut self, text: &str) {
        self.0.show_client_local(text);
    }
    fn set_character_option(&mut self, option_id: u32, value: bool) {
        self.0.set_character_option(option_id, value);
    }
    fn request_allegiance_info(&mut self, name: &str) {
        self.0.send(ClientRequest::AllegianceInfo(name.to_owned()));
    }
}

/// `/fillcomps [clear | <category>] [<price>]`: clear the component book's
/// desired amounts, or fill the open vendor's buy list from them.
/// `/saveui [name]`, `/loadui [name]`: at most one name of at most 16
/// characters (none: the default profile).
fn ui_profile(arguments: &str, save: bool, host: &mut impl ClientCommandHost) {
    let parts: Vec<&str> = arguments.split_whitespace().collect();
    let command = if save { "saveui" } else { "loadui" };
    if parts.len() > 1 {
        host.show_system_message(&format!("Please use @help {command} for proper usage."));
        return;
    }
    let name = parts.first().copied().unwrap_or("").to_owned();
    if name.chars().count() > 16 {
        host.show_system_message("The file name must be 16 characters or less.");
        return;
    }
    host.window(if save {
        WindowRequest::SaveUi(name)
    } else {
        WindowRequest::LoadUi(name)
    });
}

/// `/render`: the usage, or a landscape radius or field of view in
/// retail's ranges, read as C's `atoi` reads them. Any other option is
/// ignored, as retail does.
fn render_option(arguments: &str, host: &mut impl ClientCommandHost) {
    let parts: Vec<&str> = arguments.split_whitespace().collect();
    let usage = super::help::RENDER.trim_end_matches('\n');
    let Some(&option) = parts.first() else {
        host.show_system_message(usage);
        return;
    };
    if option.eq_ignore_ascii_case("usage") {
        host.show_system_message(usage);
    } else if option.eq_ignore_ascii_case("radius") {
        let Some(&value) = parts.get(1) else {
            host.show_system_message("Must specify a radius");
            return;
        };
        let radius = retail_atoi(value);
        if !(5..=25).contains(&radius) {
            host.show_system_message("Radius must be between 5 and 25");
            return;
        }
        host.window(WindowRequest::SetLandscapeRadius(radius));
        host.show_system_message("Landscape radius set");
    } else if option.eq_ignore_ascii_case("fov") {
        let Some(&value) = parts.get(1) else {
            host.show_system_message("Must specify a field of view");
            return;
        };
        let degrees = retail_atoi(value);
        if !(10..=160).contains(&degrees) {
            host.show_system_message("Field of view must be between 10 and 160");
            return;
        }
        host.window(WindowRequest::SetFieldOfView(degrees));
        host.show_system_message("Field of view set");
    }
}

/// C's `atoi`: an optional sign, then the leading digits (none: 0),
/// clamped to the `i32` range.
fn retail_atoi(value: &str) -> i32 {
    let (negative, digits) = match value.as_bytes().first() {
        Some(b'-') => (true, &value[1..]),
        Some(b'+') => (false, &value[1..]),
        _ => (false, value),
    };
    let limit = i64::from(i32::MAX) + i64::from(negative);
    let mut result: i64 = 0;
    for byte in digits.bytes().take_while(u8::is_ascii_digit) {
        result = (result * 10 + i64::from(byte - b'0')).min(limit);
    }
    let signed = if negative { -result } else { result };
    i32::try_from(signed).unwrap_or(if negative { i32::MIN } else { i32::MAX })
}

/// True when a command that takes no arguments got none; otherwise its
/// usage is shown.
fn require_no_arguments(arguments: &str, usage: &str, host: &mut impl ClientCommandHost) -> bool {
    if arguments.trim().is_empty() {
        return true;
    }
    host.show_system_message(&format!("Usage: {usage}"));
    false
}

fn fill_components(arguments: &str, host: &mut impl ClientCommandHost) {
    use ac_core::items::component_fill as fill;
    let parts: Vec<&str> = arguments.split_whitespace().collect();
    if parts.len() > 2 {
        host.show_system_message("Please use @help fillcomps for proper usage.");
        return;
    }
    if parts
        .first()
        .is_some_and(|p| p.eq_ignore_ascii_case("clear"))
    {
        host.request(LocalRequest::ClearDesiredComponents);
        host.show_system_message("Component list cleared.");
        return;
    }
    let mut category = None;
    let mut maximum_price = 0;
    for part in parts {
        if let Ok(price) = part.parse::<u32>() {
            if price == 0 {
                host.show_system_message("Please specify a value greater than 0.");
                return;
            }
            maximum_price = price;
            continue;
        }
        // The numbering the component table groups by.
        category = Some(match part.to_ascii_lowercase().as_str() {
            "scarab" | "scarabs" => fill::SCARAB_CATEGORY,
            "herb" | "herbs" => fill::HERB_CATEGORY,
            "powderedgem" | "powderedgems" | "powder" | "powders" => fill::POWDERED_GEM_CATEGORY,
            "alchemicalsubstance" | "alchemicalsubstances" | "potion" | "potions" => {
                fill::ALCHEMICAL_SUBSTANCE_CATEGORY
            }
            "talisman" | "talismans" => fill::TALISMAN_CATEGORY,
            "taper" | "tapers" => fill::TAPER_CATEGORY,
            "pea" | "peas" => fill::PEA_CATEGORY,
            _ => {
                host.show_system_message("Invalid component type specified.");
                return;
            }
        });
    }
    if !host.has_open_vendor() {
        host.show_system_message("You need an open vendor.");
        return;
    }
    host.request(LocalRequest::FillComponents {
        category: category.unwrap_or(fill::ANY_CATEGORY),
        maximum_price,
    });
}

/// `None` when the player is not described yet: the client then sends and
/// lets the server decide, rather than inventing a refusal.
fn has_player_flag(host: &impl ClientCommandHost, flag: PublicWeenieFlags) -> Option<bool> {
    host.player_public_weenie_bitfield()
        .map(|bits| PublicWeenieFlags(bits).intersects(flag))
}

fn chat_log_file(arguments: &str, host: &mut impl ClientCommandHost) {
    let name = arguments.trim();
    let result = host.set_chat_log_file(name);
    if result.closed {
        host.show_system_message(&format!(
            "Chat log {} closed.",
            result.closed_name.as_deref().unwrap_or_default()
        ));
    }
    if name.is_empty() {
        host.show_system_message(if result.closed {
            "Chat output now directed only to the screen."
        } else {
            "Please specify a file to append chat messages to."
        });
        return;
    }
    host.show_system_message(&if result.opened {
        format!(
            "Copying chat to {}.  Run command again with no arguments to turn off logging.",
            result.name
        )
    } else {
        format!("Failed to redirect to file {}!", result.name)
    });
}

fn away(arguments: &str, host: &mut impl ClientCommandHost) {
    let first = first_argument(arguments);
    if first.is_empty() || first.eq_ignore_ascii_case("on") {
        if !host.is_away() {
            host.send(ClientRequest::SetAfkMode(true));
        }
    } else if first.eq_ignore_ascii_case("off") {
        if host.is_away() {
            host.send(ClientRequest::SetAfkMode(false));
        }
    } else if first.eq_ignore_ascii_case("msg") {
        let mut message: String = remainder_after_first_argument(arguments)
            .trim_matches(' ')
            .chars()
            .take(191)
            .collect();
        if !message.is_empty() && !message.contains('\n') {
            message.push('\n');
        }
        let notice = if message.is_empty() {
            "New AFK message set: I am currently away from the keyboard.".to_owned()
        } else {
            format!("New AFK message set: {message}")
        };
        host.send(ClientRequest::SetAfkMessage(message));
        host.show_system_message(&notice);
    } else {
        host.show_system_message(AWAY_HELP);
    }
}

fn consent(arguments: &str, host: &mut impl ClientCommandHost) {
    let first = first_argument(arguments);
    if first.eq_ignore_ascii_case("on") {
        if !host.character_option(option_id::ACCEPT_LOOT_PERMITS) {
            host.set_character_option(option_id::ACCEPT_LOOT_PERMITS, true);
        }
        host.show_system_message(
            "You can now accept corpse looting permissions from other players.",
        );
    } else if first.eq_ignore_ascii_case("off") {
        if host.character_option(option_id::ACCEPT_LOOT_PERMITS) {
            host.set_character_option(option_id::ACCEPT_LOOT_PERMITS, false);
        }
        host.show_system_message(
            "You are no longer accepting corpse looting permissions from other players.",
        );
    } else if first.eq_ignore_ascii_case("who") {
        host.send(ClientRequest::DisplayConsent);
    } else if first.eq_ignore_ascii_case("clear") {
        host.send(ClientRequest::ClearConsent);
    } else if first.eq_ignore_ascii_case("remove") {
        let name = remainder_after_first_argument(arguments).trim();
        if name.is_empty() {
            host.show_system_message("Please specify a person to remove from your consent list.");
        } else {
            host.send(ClientRequest::RemoveConsent(name.to_owned()));
        }
    } else {
        host.show_system_message("Please specify a valid consent command.");
    }
}

fn friends(arguments: &str, host: &mut impl ClientCommandHost) {
    let operation = first_argument(arguments);
    if operation.is_empty() {
        display_friends(false, host);
        return;
    }
    if operation.eq_ignore_ascii_case("online") {
        display_friends(true, host);
        return;
    }
    let remainder = remainder_after_first_argument(arguments);
    if operation.eq_ignore_ascii_case("add") {
        add_friend(remainder, host);
    } else if operation.eq_ignore_ascii_case("remove") {
        remove_friend(remainder, host);
    } else if operation.eq_ignore_ascii_case("old") && remainder.trim().is_empty() {
        host.send(ClientRequest::LegacyFriendsList);
    } else {
        host.show_system_message("Invalid friends command specified.");
    }
}

fn add_friend(arguments: &str, host: &mut impl ClientCommandHost) {
    let name = arguments.trim();
    if name.is_empty() {
        host.show_system_message("You must specify the name of the friend you wish to add.");
    } else if host.friends().len() >= MAX_FRIENDS {
        host.show_weenie_error(0x0561);
    } else {
        host.send(ClientRequest::AddFriend(name.to_owned()));
    }
}

fn remove_friend(arguments: &str, host: &mut impl ClientCommandHost) {
    let name = arguments.trim();
    if name.is_empty() {
        host.show_system_message("You must specify the name of the friend you wish to remove.");
        return;
    }
    if name.eq_ignore_ascii_case("-all") {
        host.send(ClientRequest::ClearFriends);
        host.clear_friends_locally();
        host.show_system_message("Your friends list has been cleared.\n");
        return;
    }
    let id = host
        .friends()
        .entries()
        .iter()
        .find(|f| f.name.to_lowercase() == name.to_lowercase())
        .map(|f| f.id);
    match id {
        Some(id) => host.send(ClientRequest::RemoveFriend(id)),
        None => host.show_weenie_error(0x0563),
    }
}

fn display_friends(online_only: bool, host: &mut impl ClientCommandHost) {
    let entries = host.friends().entries();
    if entries.is_empty() {
        host.show_system_message("Your friends list is empty!\n");
        return;
    }
    let lines: Vec<String> = entries
        .iter()
        .filter(|f| !online_only || f.online)
        .map(|f| format!("  {}{}", f.name, if f.online { " (Online)" } else { "" }))
        .collect();
    let text = if lines.is_empty() {
        "Your friends:\n  You have no friends that are online.\n".to_owned()
    } else {
        format!("Your friends:\n{}\n", lines.join("\n"))
    };
    host.show_system_message(&text);
}

fn squelch(arguments: &str, add: bool, host: &mut impl ClientCommandHost) {
    if arguments.trim().is_empty() {
        display_character_squelches(host);
        return;
    }
    let parts: Vec<&str> = arguments.split_whitespace().collect();
    let mut account = false;
    let mut message_type = 1;
    let mut reply_name = None;
    let mut index = 0;
    while let Some(option) = parts.get(index).and_then(|p| p.strip_prefix('-')) {
        if option.eq_ignore_ascii_case("account") {
            account = true;
        } else if option.eq_ignore_ascii_case("reply") {
            match host.last_teller().filter(|t| !t.trim().is_empty()) {
                Some(teller) => reply_name = Some(teller.to_owned()),
                None => {
                    host.show_system_message(
                        "A player must @tell you before you can use the -reply option.",
                    );
                    return;
                }
            }
        } else if let Some(kind) = message_type_named(option) {
            message_type = kind;
        } else {
            host.show_system_message(&format!("\"{option}\" is not a valid squelch category."));
            return;
        }
        index += 1;
    }
    let name = reply_name.unwrap_or_else(|| parts[index..].join(" "));
    if name.is_empty() {
        host.show_system_message("You have not specified a squelch target.");
        return;
    }
    host.send(if account {
        ClientRequest::AccountSquelch { add, name }
    } else {
        ClientRequest::CharacterSquelch {
            add,
            character_id: 0,
            name,
            message_type,
        }
    });
}

fn global_filter(arguments: &str, add: bool, host: &mut impl ClientCommandHost) {
    if arguments.trim().is_empty() {
        let global = &host.squelch().global;
        let list = if global.message_types.is_empty() {
            "none".to_owned()
        } else {
            format_message_types(&global.message_types)
        };
        host.show_system_message(&format!(
            "The following types of messages are currently being filtered globally:\n{list}\n(For a list of filter options, type @help filter)\n"
        ));
        return;
    }
    let parts: Vec<&str> = arguments.split_whitespace().collect();
    let [only] = parts.as_slice() else {
        host.show_system_message("Incorrect usage, use @help for proper usage.");
        return;
    };
    let Some(name) = only.strip_prefix('-') else {
        host.show_system_message("Incorrect usage, use @help for proper usage.");
        return;
    };
    match message_type_named(name) {
        Some(kind) if kind != 1 => host.send(ClientRequest::GlobalSquelch {
            add,
            message_type: kind,
        }),
        _ => host.show_system_message("You must specify a valid message type."),
    }
}

fn display_character_squelches(host: &mut impl ClientCommandHost) {
    let mut squelched: Vec<_> = host
        .squelch()
        .characters
        .iter()
        .filter(|(_, info)| !info.message_types.is_empty())
        .collect();
    squelched.sort_by_key(|(id, _)| **id);
    let lines: Vec<String> = squelched
        .into_iter()
        .map(|(_, info)| {
            format!(
                "Name: {}{}{}",
                info.name,
                if info.account_wide {
                    " (account) "
                } else {
                    " "
                },
                format_message_types(&info.message_types)
            )
        })
        .collect();
    let list = if lines.is_empty() {
        "none\n".to_owned()
    } else {
        format!("{}\n", lines.join("\n"))
    };
    host.show_system_message(&format!(
        "(account) denotes a character whose account has also been squelched.\nFormat: Name : List of squelched message types.\n--------\n{list}"
    ));
}

fn format_message_types(types: &std::collections::HashSet<u32>) -> String {
    if types.contains(&1) {
        return "All message types".to_owned();
    }
    MESSAGE_TYPES
        .iter()
        .filter(|(id, _)| *id != 1 && types.contains(id))
        .map(|(_, name)| *name)
        .collect::<Vec<_>>()
        .join(", ")
}

fn message_type_named(name: &str) -> Option<u32> {
    MESSAGE_TYPES
        .iter()
        .find(|(_, known)| known.eq_ignore_ascii_case(name))
        .map(|(id, _)| *id)
}

fn first_argument(arguments: &str) -> &str {
    let trimmed = arguments.trim();
    trimmed
        .split([' ', '\t', '\r', '\n'])
        .next()
        .unwrap_or_default()
}

fn remainder_after_first_argument(arguments: &str) -> &str {
    let trimmed = arguments.trim();
    trimmed
        .find([' ', '\t', '\r', '\n'])
        .map_or("", |at| trimmed[at + 1..].trim_start())
}

/// The squelch and filter message types, in listing order.
const MESSAGE_TYPES: &[(u32, &str)] = &[
    (1, "All"),
    (2, "Speech"),
    (3, "Tell"),
    (6, "Combat"),
    (7, "Magic"),
    (12, "Emote"),
    (16, "Appraisal"),
    (17, "Spellcasting"),
    (18, "Allegiance"),
    (19, "Fellowship"),
    (21, "Combat_Enemy"),
    (22, "Combat_Self"),
    (23, "Recall"),
    (24, "Craft"),
    (25, "Salvaging"),
];

const STANDARD_EMOTES: &str = "Standard Emotes:\nNote: These commands should be bound on either side by asterisks. (Example: *wave*)\nShakeFist; Beckon; BeSeeingYou; BlowKiss; BowDeep; ClapHands; Cry; Laugh; Nod; Point; Shrug; Wave; Akimbo; HeartyLaugh; Salute; TapFoot; WaveHigh; WaveLow; Yawn; Stretch; Cringe; Kneel; Plead; Shiver; Shoo; Slouch; Spit; Surrender; Woah; Winded; YMCA; Eat; Drink; Teapot; Pray; Mock; Cheer; Helper; Warm Hands; Scratch Head; Shake Head\n\n";

const ENDURANCE_TEXT: &str = "The endurance attribute has a number of abilities tied to it.\n\
First, some combination of strength and endurance (with endurance being more important) now allows one to regenerate hit points at a faster rate the higher one's endurance is.  This bonus is in addition to any regeneration spells one may have placed upon themselves.  This endurance regeneration bonus caps at around 110%.\n\
Second, the higher a player's Endurance, the less stamina one uses while attacking.  This benefit is tied to Endurance only, and it caps out at around 50% less stamina used per attack.  The minimum stamina used per attack remains one.\n\
Third, the higher a player's Endurance, the more likely they are not to use a point of stamina to successfully evade a missile or melee attack.  A player is required to have Melee Defense for melee attacks or Missile Defense for missile attacks trained or specialized in order for this specific ability to work.  This benefit is tied to Endurance only, and it caps out at around a 75% chance to avoid losing a point of stamina per successful evasion.\n\
Fourth, some combination of strength and endurance (the two are roughly of equivalent importance) now allows one to partially resist drain and harm attacks, up to a maximum of roughly 50%.\n\
Fifth, some combination of strength and endurance (the two are roughly of equivalent importance) now allows one to have a level of \"natural resistances\" to the 7 damage types, the same as a certain level of life protections.  This caps out at a 50% resistance (the equivalent to level 5 life prots) to these damage types.  This resistance is not additive to life protections: higher level life protections will overwrite these natural resistances, although life vulns will take these natural resistances into account, if the player does not have a higher level life protection cast upon him.\n\
The natural resistances, drain resistances, and regeneration rate info are now visible on the Character Information Panel, in what was once the Burden panel.  This panel now displays the above three Endurance benefits, the burden info, as well as information about your age, birth date, and number of deaths.\n\
The 5 categories for the endurance benefits are, in order from lowest benefit to highest: Poor, Mediocre, Hardy, Resilient, and Indomitable, with each range of benefits divided up equally amongst the 5 (e.g. Poor describes having anywhere from 1-10% resistance against drain health attacks, etc.).\n";

const AWAY_HELP: &str = "@afk - Turns on AFK (away-from-keyboard) mode. When set to AFK, other players that send you directed chatyou will receive a customizable message that your are not currently at the keyboard.\n\
@afk on - Turns on AFK mode. When set to AFK, other players that send you directed chatyou will receive a customizable message that your are not currently at the keyboard.\n\
@afk off - Turn off AFK mode.\n\
@afk msg <message> - Set the message that will be sent to players that send you directed chat while you are in AFK mode. Issuing \"@afk msg\" with no message will set your AFK message back to the default. Your custom AFK message is limited to 192 characters.\n";

#[cfg(test)]
mod tests {
    use super::*;
    use ac_core::social::{FriendEntry, FriendsUpdate, FriendsUpdateType};

    #[derive(Default)]
    struct Recorder {
        calls: Vec<String>,
        messages: Vec<String>,
        errors: Vec<u32>,
        bitfield: Option<u32>,
        away: bool,
        friends: FriendsState,
        squelch: SquelchDatabase,
        last_teller: Option<String>,
        log: Option<ChatLogResult>,
        options: crate::character::CharacterOptions,
        option_sets: Vec<(u32, bool)>,
        corpse_cell: Option<u32>,
        position: Option<(u32, [f32; 3], [f32; 4])>,
        vendor_open: bool,
    }

    impl Recorder {
        fn new() -> Self {
            Self {
                bitfield: Some(0x0200_0028),
                ..Self::default()
            }
        }
    }

    impl ClientCommandHost for Recorder {
        fn send(&mut self, request: ClientRequest) {
            self.calls.push(format!("{request:?}"));
        }
        fn show_system_message(&mut self, text: &str) {
            self.messages.push(text.to_owned());
        }
        fn show_client_local(&mut self, text: &str) {
            self.messages.push(format!("local:{text}"));
        }
        fn show_weenie_error(&mut self, code: u32) {
            self.errors.push(code);
        }
        fn player_public_weenie_bitfield(&self) -> Option<u32> {
            self.bitfield
        }
        fn is_away(&self) -> bool {
            self.away
        }
        fn clear_chat(&mut self, all: bool) {
            self.calls.push(format!("clear:{all}"));
        }
        fn set_chat_log_file(&mut self, name: &str) -> ChatLogResult {
            self.calls.push(format!("log:{name}"));
            self.log.clone().unwrap_or(ChatLogResult {
                opened: !name.is_empty(),
                closed: name.is_empty(),
                name: name.to_owned(),
                closed_name: Some("old.txt".into()),
            })
        }
        fn friends(&self) -> &FriendsState {
            &self.friends
        }
        fn clear_friends_locally(&mut self) {
            self.friends.clear();
        }
        fn squelch(&self) -> &SquelchDatabase {
            &self.squelch
        }
        fn last_teller(&self) -> Option<&str> {
            self.last_teller.as_deref()
        }
        fn is_using_turbine_chat(&self) -> bool {
            true
        }
        fn client_version(&self) -> &str {
            "1.2.3"
        }
        fn character_option(&self, option_id: u32) -> bool {
            self.options.get(option_id)
        }
        fn set_character_option(&mut self, option_id: u32, value: bool) {
            self.options.set_bit(option_id, value);
            self.option_sets.push((option_id, value));
        }
        fn current_position(&self) -> Option<(u32, [f32; 3], [f32; 4])> {
            self.position
        }
        fn last_outside_corpse_cell(&self) -> Option<u32> {
            self.corpse_cell
        }
        fn ask_confirmation(&mut self, confirmation: ClientConfirmation) {
            self.calls.push(format!("confirm:{}", confirmation.text()));
        }
        fn has_open_vendor(&self) -> bool {
            self.vendor_open
        }
        fn request(&mut self, request: LocalRequest) {
            self.calls.push(format!("{request:?}"));
        }
        fn window(&mut self, request: WindowRequest) {
            self.calls.push(match request {
                WindowRequest::ToggleFrameRate => "fps".to_owned(),
                WindowRequest::SaveUi(n) => format!("saveui:{n}"),
                WindowRequest::LoadUi(n) => format!("loadui:{n}"),
                WindowRequest::SaveAutoUi => "saveautoui".to_owned(),
                WindowRequest::LoadAutoUi => "loadautoui".to_owned(),
                WindowRequest::SetLandscapeRadius(r) => format!("radius:{r}"),
                WindowRequest::SetFieldOfView(f) => format!("fov:{f}"),
                WindowRequest::GameplayOptionsPage => "gameplay-options".to_owned(),
                other => format!("{other:?}"),
            });
        }
    }

    fn run(host: &mut Recorder, command: ClientCommandId, arguments: &str) {
        execute(command, arguments, host);
    }

    // Golden behaviour: OpenAC tests/AcDream.Runtime.Tests/Chat/RuntimeClientCommandDispatcherTests.cs

    #[test]
    fn recall_and_query_commands_send_their_requests() {
        let mut host = Recorder::new();
        for id in [
            ClientCommandId::LifestoneRecall,
            ClientCommandId::MarketplaceRecall,
            ClientCommandId::PkArenaRecall,
            ClientCommandId::PkLiteArenaRecall,
            ClientCommandId::HouseRecall,
            ClientCommandId::MansionRecall,
            ClientCommandId::QueryAge,
            ClientCommandId::QueryBirth,
        ] {
            run(&mut host, id, "");
        }
        assert_eq!(
            host.calls,
            [
                "TeleportToLifestone",
                "TeleportToMarketplace",
                "TeleportToPkArena",
                "TeleportToPkLiteArena",
                "TeleportToHouse",
                "TeleportToMansion",
                "QueryAge",
                "QueryBirth"
            ]
        );
    }

    #[test]
    fn pk_gates_refuse_with_the_retail_errors() {
        for (bitfield, command, call, error) in [
            (
                Some(0x8),
                ClientCommandId::PkArenaRecall,
                None,
                Some(0x055F),
            ),
            (
                Some(0x8),
                ClientCommandId::PkLiteArenaRecall,
                None,
                Some(0x0560),
            ),
            (
                Some(0x8),
                ClientCommandId::EnterPkLite,
                Some("EnterPkLite"),
                None,
            ),
            (Some(0x20), ClientCommandId::EnterPkLite, None, Some(0x0507)),
            (
                Some(0x0200_0000),
                ClientCommandId::EnterPkLite,
                None,
                Some(0x0507),
            ),
            (
                None,
                ClientCommandId::EnterPkLite,
                Some("EnterPkLite"),
                None,
            ),
            (
                None,
                ClientCommandId::PkArenaRecall,
                Some("TeleportToPkArena"),
                None,
            ),
        ] {
            let mut host = Recorder {
                bitfield,
                ..Recorder::default()
            };
            run(&mut host, command, "");
            assert_eq!(host.calls.first().map(String::as_str), call, "{command:?}");
            assert_eq!(host.errors.first().copied(), error, "{command:?}");
        }
    }

    #[test]
    fn version_names_turbine_chat() {
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::ShowVersion, "");
        assert_eq!(host.messages, ["Client version 1.2.3\nUsing Turbine Chat."]);
    }

    #[test]
    fn away_respects_the_current_mode_and_packs_the_message() {
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::Away, "on");
        run(&mut host, ClientCommandId::Away, "msg Stepped away");
        assert_eq!(
            host.calls,
            ["SetAfkMode(true)", "SetAfkMessage(\"Stepped away\\n\")"]
        );
        assert_eq!(host.messages, ["New AFK message set: Stepped away\n"]);
        let mut host = Recorder {
            away: true,
            ..Recorder::new()
        };
        run(&mut host, ClientCommandId::Away, "off");
        assert_eq!(host.calls, ["SetAfkMode(false)"]);
    }

    #[test]
    fn consent_and_emote_route_their_actions() {
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::Consent, "who");
        run(&mut host, ClientCommandId::Consent, "clear");
        run(&mut host, ClientCommandId::Consent, "remove Alice Example");
        run(&mut host, ClientCommandId::Emote, "waves happily");
        assert_eq!(
            host.calls,
            [
                "DisplayConsent",
                "ClearConsent",
                "RemoveConsent(\"Alice Example\")",
                "Emote(\"waves happily\")"
            ]
        );
    }

    #[test]
    fn friends_commands_read_the_list_and_send_the_object_id() {
        let mut host = Recorder::new();
        host.friends.apply(&FriendsUpdate {
            update_type: FriendsUpdateType::FULL,
            entries: vec![
                FriendEntry {
                    id: 0x5000_0001,
                    name: "Alice".into(),
                    online: true,
                    appear_offline: false,
                    friends: vec![],
                    friend_of: vec![],
                },
                FriendEntry {
                    id: 0x5000_0002,
                    name: "Bjørn".into(),
                    online: false,
                    appear_offline: false,
                    friends: vec![],
                    friend_of: vec![],
                },
            ],
        });
        run(&mut host, ClientCommandId::Friends, "online");
        run(&mut host, ClientCommandId::FriendsRemove, "alice");
        run(&mut host, ClientCommandId::FriendsAdd, "Cara");
        run(&mut host, ClientCommandId::Friends, "old");
        assert!(host.messages[0].contains("Alice (Online)"));
        assert!(!host.messages[0].contains("Bjørn"));
        assert_eq!(
            host.calls,
            [
                "RemoveFriend(1342177281)",
                "AddFriend(\"Cara\")",
                "LegacyFriendsList"
            ]
        );
        run(&mut host, ClientCommandId::FriendsRemove, "nobody");
        assert_eq!(host.errors, [0x0563]);
        run(&mut host, ClientCommandId::FriendsRemove, "-all");
        assert!(host.friends.is_empty());
        assert_eq!(host.calls.last().unwrap(), "ClearFriends");
    }

    #[test]
    fn squelch_and_filter_parse_the_retail_options() {
        let mut host = Recorder {
            last_teller: Some("Recent Teller".into()),
            ..Recorder::new()
        };
        run(&mut host, ClientCommandId::Squelch, "-account Alice");
        run(&mut host, ClientCommandId::Unsquelch, "-reply");
        run(&mut host, ClientCommandId::Squelch, "-Magic Caster Name");
        run(&mut host, ClientCommandId::Filter, "-Combat_Self");
        run(&mut host, ClientCommandId::Unfilter, "-Tell");
        assert_eq!(
            host.calls,
            [
                "AccountSquelch { add: true, name: \"Alice\" }",
                "CharacterSquelch { add: false, character_id: 0, name: \"Recent Teller\", message_type: 1 }",
                "CharacterSquelch { add: true, character_id: 0, name: \"Caster Name\", message_type: 7 }",
                "GlobalSquelch { add: true, message_type: 22 }",
                "GlobalSquelch { add: false, message_type: 3 }",
            ]
        );
        run(&mut host, ClientCommandId::Filter, "-All");
        assert_eq!(
            host.messages.last().unwrap(),
            "You must specify a valid message type."
        );
        run(&mut host, ClientCommandId::Squelch, "-nonsense Bob");
        assert_eq!(
            host.messages.last().unwrap(),
            "\"nonsense\" is not a valid squelch category."
        );
    }

    #[test]
    fn chat_and_notell_toggles_are_global_squelches() {
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::ChatToggle, "off");
        run(&mut host, ClientCommandId::NoTellToggle, "on");
        assert_eq!(
            host.calls,
            [
                "GlobalSquelch { add: true, message_type: 2 }",
                "GlobalSquelch { add: true, message_type: 3 }"
            ]
        );
    }

    // Golden behaviour: OpenAC src/AcDream.Runtime/Chat/RuntimeClientCommandBindings.cs
    // (`SetChatTitle: _ => { }`), walked with "Adventurer" by
    // tests/AcDream.HostParity.Tests/Scenarios/ClientCommandParityTests.cs.
    #[test]
    fn title_is_a_silent_no_op() {
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::SetChatTitle, "Adventurer");
        run(&mut host, ClientCommandId::SetChatTitle, "");
        assert!(host.calls.is_empty());
        assert!(host.messages.is_empty());
        assert!(host.errors.is_empty());
        assert!(host.option_sets.is_empty());
    }

    #[test]
    fn channel_argument_commands_resolve_tags() {
        let mut host = Recorder::new();
        for command in [
            ClientCommandId::ListChannel,
            ClientCommandId::OnChannel,
            ClientCommandId::OffChannel,
        ] {
            run(&mut host, command, "nonsense");
        }
        assert!(host.calls.is_empty());
        assert_eq!(host.errors, [0x0422, 0x0422, 0x0422]);
        for (command, tag, call) in [
            (
                ClientCommandId::ListChannel,
                "fellowship",
                "ListChannel(2048)",
            ),
            (ClientCommandId::OnChannel, "admin", "OnChannel(2)"),
            (ClientCommandId::OffChannel, "sentinel", "OffChannel(512)"),
            (
                ClientCommandId::ListChannel,
                "allegiance",
                "ListChannel(33554432)",
            ),
        ] {
            let mut host = Recorder::new();
            run(&mut host, command, tag);
            assert_eq!(host.calls, [call]);
        }
    }

    #[test]
    fn permit_joins_a_spaced_name() {
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::Permit, "add Aunt Agatha");
        run(
            &mut host,
            ClientCommandId::Permit,
            "remove Lord Gnarly Beard",
        );
        assert_eq!(
            host.calls,
            [
                "AddPlayerPermission(\"Aunt Agatha\")",
                "RemovePlayerPermission(\"Lord Gnarly Beard\")"
            ]
        );
    }

    #[test]
    fn log_reports_where_chat_goes_and_how_to_stop() {
        for (log, argument, expected) in [
            (
                ChatLogResult {
                    opened: true,
                    closed: false,
                    name: "aclog.txt".into(),
                    closed_name: None,
                },
                "aclog.txt",
                vec![
                    "Copying chat to aclog.txt.  Run command again with no arguments to turn off logging.",
                ],
            ),
            (
                ChatLogResult {
                    opened: false,
                    closed: false,
                    name: "C:/nope/x.txt".into(),
                    closed_name: None,
                },
                "C:/nope/x.txt",
                vec!["Failed to redirect to file C:/nope/x.txt!"],
            ),
            (
                ChatLogResult {
                    opened: false,
                    closed: true,
                    name: String::new(),
                    closed_name: Some("aclog.txt".into()),
                },
                "",
                vec![
                    "Chat log aclog.txt closed.",
                    "Chat output now directed only to the screen.",
                ],
            ),
            (
                ChatLogResult {
                    opened: false,
                    closed: false,
                    name: String::new(),
                    closed_name: None,
                },
                "",
                vec!["Please specify a file to append chat messages to."],
            ),
        ] {
            let mut host = Recorder {
                log: Some(log),
                ..Recorder::new()
            };
            run(&mut host, ClientCommandId::ChatLogFile, argument);
            assert_eq!(host.messages, expected);
        }
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::ChatLogFile, "my chat log.txt");
        assert_eq!(host.calls, ["log:my chat log.txt"]);
        let mut host = Recorder {
            log: Some(ChatLogResult {
                opened: true,
                closed: true,
                name: "new.txt".into(),
                closed_name: Some("old.txt".into()),
            }),
            ..Recorder::new()
        };
        run(&mut host, ClientCommandId::ChatLogFile, "new.txt");
        assert_eq!(host.messages[0], "Chat log old.txt closed.");
        assert!(host.messages[1].starts_with("Copying chat to new.txt."));
    }

    #[test]
    fn chat_and_layout_commands_use_the_named_and_automatic_profiles() {
        // OpenAC ChatAndLayoutCommands_UseNamedAndAutomaticPersistenceBindings
        // and the frame-rate half of its window-hook case.
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::ClearChat, "all");
        run(&mut host, ClientCommandId::SaveUi, "hunt");
        run(&mut host, ClientCommandId::LoadUi, "hunt");
        run(&mut host, ClientCommandId::SaveAutoUi, "");
        run(&mut host, ClientCommandId::LoadAutoUi, "");
        run(
            &mut host,
            ClientCommandId::SaveUi,
            "this-name-is-over-16-characters",
        );
        run(&mut host, ClientCommandId::ToggleFrameRate, "");
        assert_eq!(
            host.calls,
            [
                "clear:true",
                "saveui:hunt",
                "loadui:hunt",
                "saveautoui",
                "loadautoui",
                "fps"
            ]
        );
        assert_eq!(
            host.messages,
            ["The file name must be 16 characters or less."]
        );
    }

    #[test]
    fn render_radius_accepts_the_retail_range_and_an_atoi_prefix() {
        // OpenAC RenderRadius_AcceptsRetailRangeAndAtoiPrefix.
        for (arguments, expected) in [
            ("radius 5", 5),
            ("RADIUS 25 extra ignored", 25),
            ("radius 12suffix", 12),
        ] {
            let mut host = Recorder::new();
            run(&mut host, ClientCommandId::RenderOption, arguments);
            assert_eq!(host.calls, [format!("radius:{expected}")], "{arguments}");
            assert_eq!(host.messages, ["Landscape radius set"], "{arguments}");
        }
    }

    #[test]
    fn render_fov_accepts_the_retail_range_and_an_atoi_prefix() {
        // OpenAC RenderFov_AcceptsRetailRangeAndAtoiPrefix.
        for (arguments, expected) in [
            ("fov 10", 10),
            ("FOV 160 extra", 160),
            ("fov 91degrees", 91),
        ] {
            let mut host = Recorder::new();
            run(&mut host, ClientCommandId::RenderOption, arguments);
            assert_eq!(host.calls, [format!("fov:{expected}")], "{arguments}");
            assert_eq!(host.messages, ["Field of view set"], "{arguments}");
        }
    }

    #[test]
    fn render_invalid_values_print_retails_exact_reply() {
        // OpenAC Render_InvalidValuesPrintRetailsExactReply.
        for (arguments, expected) in [
            ("radius", "Must specify a radius"),
            ("radius 4", "Radius must be between 5 and 25"),
            ("radius nope", "Radius must be between 5 and 25"),
            ("fov", "Must specify a field of view"),
            ("fov 161", "Field of view must be between 10 and 160"),
        ] {
            let mut host = Recorder::new();
            run(&mut host, ClientCommandId::RenderOption, arguments);
            assert!(host.calls.is_empty(), "{arguments}");
            assert_eq!(host.messages, [expected], "{arguments}");
        }
    }

    #[test]
    fn render_usage_and_unknown_option_match_retail() {
        // OpenAC Render_UsageAndUnknownOptionMatchRetail.
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::RenderOption, "");
        run(&mut host, ClientCommandId::RenderOption, "usage");
        run(&mut host, ClientCommandId::RenderOption, "unknown 1");
        let usage = crate::communication::help::RENDER.trim_end_matches('\n');
        assert_eq!(host.messages, [usage, usage]);
        assert!(host.calls.is_empty());
    }

    #[test]
    fn retail_atoi_reads_a_sign_and_the_leading_digits() {
        // OpenAC RuntimeClientCommandDispatcher.RetailAtoi.
        assert_eq!(retail_atoi("12suffix"), 12);
        assert_eq!(retail_atoi("-7"), -7);
        assert_eq!(retail_atoi("+3x"), 3);
        assert_eq!(retail_atoi("nope"), 0);
        assert_eq!(retail_atoi(""), 0);
        assert_eq!(retail_atoi("99999999999"), i32::MAX);
        assert_eq!(retail_atoi("-99999999999"), i32::MIN);
    }

    #[test]
    fn day_toggles_the_persistent_option_and_prints_the_retail_lines() {
        // OpenAC Day_TogglesThePersistentOptionAndPrintsRetailsExactLines.
        let mut host = Recorder::default();
        run(
            &mut host,
            ClientCommandId::TogglePersistentDaylight,
            "ignored exactly like retail",
        );
        run(&mut host, ClientCommandId::TogglePersistentDaylight, "");
        assert_eq!(
            host.option_sets,
            [
                (option_id::PERSISTENT_AT_DAY, true),
                (option_id::PERSISTENT_AT_DAY, false)
            ]
        );
        assert_eq!(
            host.messages,
            ["Let there be light!", "Normality has been restored."]
        );
    }

    #[test]
    fn consent_on_and_off_set_the_loot_permit_option_once() {
        // OpenAC ConsentAndEmoteCommands_RouteTheirExactActions and
        // RuntimeClientCommandDispatcher.ExecuteConsent.
        let mut host = Recorder::default();
        assert!(!host.options.get(option_id::ACCEPT_LOOT_PERMITS));
        run(&mut host, ClientCommandId::Consent, "on");
        run(&mut host, ClientCommandId::Consent, "on");
        run(&mut host, ClientCommandId::Consent, "off");
        assert_eq!(
            host.option_sets,
            [
                (option_id::ACCEPT_LOOT_PERMITS, true),
                (option_id::ACCEPT_LOOT_PERMITS, false)
            ]
        );
        assert_eq!(
            host.messages,
            [
                "You can now accept corpse looting permissions from other players.",
                "You can now accept corpse looting permissions from other players.",
                "You are no longer accepting corpse looting permissions from other players.",
            ]
        );
    }

    #[test]
    fn join_and_leave_set_the_listen_option_of_the_tag() {
        // OpenAC RuntimeClientCommandDispatcher JoinChannel/LeaveChannel:
        // the tag names a listen option; an unknown tag does nothing.
        let mut host = Recorder::default();
        run(&mut host, ClientCommandId::LeaveChannel, "general");
        run(&mut host, ClientCommandId::JoinChannel, "roleplay");
        run(&mut host, ClientCommandId::JoinChannel, "nonsense");
        assert_eq!(
            host.option_sets,
            [
                (option_id::LISTEN_TO_GENERAL_CHAT, false),
                (option_id::LISTEN_TO_ROLEPLAY_CHAT, true)
            ]
        );
        assert!(host.messages.is_empty());
    }

    #[test]
    fn lockui_toggles_the_lock_option() {
        let mut host = Recorder::default();
        run(&mut host, ClientCommandId::ToggleUiLock, "");
        run(&mut host, ClientCommandId::ToggleUiLock, "");
        assert_eq!(
            host.option_sets,
            [(option_id::LOCK_UI, true), (option_id::LOCK_UI, false)]
        );
    }

    #[test]
    fn die_asks_for_the_retail_confirmation_first() {
        // Die_RequiresRetailConfirmationBeforeSuicide: the question, and the
        // suicide only once it is answered (`Communication::answer`).
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::Die, "");
        assert_eq!(
            host.calls,
            [
                "confirm:Do you really want to kill your character? You may drop items and accrue a vitae penalty."
            ]
        );
    }

    #[test]
    fn corpse_names_the_last_outside_corpse_cell() {
        // RuntimeClientCommandDispatcher ShowLastCorpseLocation with
        // RetailPositionFormatter.FormatOutdoorCell: landblock 0xA9B4, cell
        // 0x19 is Holtburg's lifestone square.
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::ShowLastCorpseLocation, "");
        host.corpse_cell = Some(0xA9B4_0019);
        run(&mut host, ClientCommandId::ShowLastCorpseLocation, "");
        // An indoor cell has no map coordinates.
        host.corpse_cell = Some(0xA9B4_0150);
        run(&mut host, ClientCommandId::ShowLastCorpseLocation, "");
        assert_eq!(
            host.messages,
            [
                "We're sorry, but we have no record of your last outside corpse location.",
                "The last time you died outside, your corpse was located at (42.1N,33.6E).",
                "We're sorry, but we have no record of your last outside corpse location.",
            ]
        );
    }

    #[test]
    fn loc_prints_the_bodys_position_in_the_retail_format() {
        // RuntimeClientCommandDispatcher ShowLocation with
        // RetailPositionFormatter.Format; the position the navigation tests
        // read back (NavigationChatCommandsTests "/nav go to Your location is: ...").
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::ShowLocation, "");
        host.position = Some((0, [1.0, 2.0, 3.0], [1.0, 0.0, 0.0, 0.0]));
        run(&mut host, ClientCommandId::ShowLocation, "");
        host.position = Some((
            0x7E03_07A3,
            [310.353_58, -312.687_32, 6.005],
            [
                std::f32::consts::FRAC_1_SQRT_2,
                0.0,
                0.0,
                -std::f32::consts::FRAC_1_SQRT_2,
            ],
        ));
        run(&mut host, ClientCommandId::ShowLocation, "");
        assert_eq!(
            host.messages,
            [
                "Not in valid cell!",
                "Not in valid cell!",
                "Your location is: 0x7E0307A3 [310.353577 -312.687317 6.005000] 0.707107 0.000000 \
                 0.000000 -0.707107",
            ]
        );
    }

    #[test]
    fn fillcomps_clear_works_without_a_vendor_and_a_fill_needs_one() {
        // FillComponents_ClearWorksWithoutVendorAndBuyingRequiresOne.
        let mut host = Recorder::new();
        run(&mut host, ClientCommandId::FillComponents, "clear");
        run(&mut host, ClientCommandId::FillComponents, "scarabs 500");
        assert_eq!(host.calls, ["ClearDesiredComponents"]);
        assert_eq!(
            host.messages,
            ["Component list cleared.", "You need an open vendor."]
        );
    }

    #[test]
    fn fillcomps_with_an_open_vendor_fills_the_buy_list() {
        // FillComponents_WithAnOpenVendor_FillsTheBuyList: no category is
        // every category (8).
        let mut host = Recorder {
            vendor_open: true,
            ..Recorder::new()
        };
        for arguments in ["", "scarabs", "500", "tapers 500"] {
            run(&mut host, ClientCommandId::FillComponents, arguments);
        }
        assert_eq!(
            host.calls,
            [
                "FillComponents { category: 8, maximum_price: 0 }",
                "FillComponents { category: 0, maximum_price: 0 }",
                "FillComponents { category: 8, maximum_price: 500 }",
                "FillComponents { category: 5, maximum_price: 500 }",
            ]
        );
        assert!(host.messages.is_empty());
    }

    #[test]
    fn fillcomps_rejects_bad_arguments_before_reaching_the_vendor() {
        // FillComponents_RejectsBadArgumentsBeforeReachingTheVendor.
        let mut host = Recorder {
            vendor_open: true,
            ..Recorder::new()
        };
        for arguments in ["scarabs 0", "widgets", "scarabs 500 extra"] {
            run(&mut host, ClientCommandId::FillComponents, arguments);
        }
        assert!(host.calls.is_empty());
        assert_eq!(
            host.messages,
            [
                "Please specify a value greater than 0.",
                "Invalid component type specified.",
                "Please use @help fillcomps for proper usage.",
            ]
        );
    }
}
