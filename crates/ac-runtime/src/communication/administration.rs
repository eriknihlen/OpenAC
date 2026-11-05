//! The `@allegiance` and `@house` administration subcommands: how each one's
//! arguments read and which request it sends. A malformed one answers in
//! the client-local text the game prints for it.

use super::catalog::ClientCommandId;
use crate::character::option_id;

/// An allegiance or house administration request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AdministrationRequest {
    /// Boot a character (or, with `account`, every character on its
    /// account) from the allegiance.
    BreakAllegianceBoot {
        /// The character.
        name: String,
        /// The whole account.
        account: bool,
    },
    /// Kick a player from the allegiance chat room for a while.
    AllegianceChatBoot {
        /// The player.
        name: String,
        /// Why.
        reason: String,
    },
    /// Gag or ungag a player in the allegiance chat room.
    AllegianceChatGag {
        /// The player.
        name: String,
        /// Gag (true) or ungag.
        enabled: bool,
    },
    /// Broadcast to the whole allegiance.
    AllegianceBroadcast(String),
    /// List the banned accounts.
    ListAllegianceBans,
    /// Ban a character's account.
    AddAllegianceBan(String),
    /// Lift a character's account ban.
    RemoveAllegianceBan(String),
    /// List the officers.
    ListAllegianceOfficers,
    /// Remove every officer.
    ClearAllegianceOfficers,
    /// Make a character an officer of a level (1 to 3).
    SetAllegianceOfficer {
        /// The character.
        name: String,
        /// The level.
        level: u32,
    },
    /// Remove an officer.
    RemoveAllegianceOfficer(String),
    /// List the officer titles.
    ListAllegianceOfficerTitles,
    /// Clear every officer title.
    ClearAllegianceOfficerTitles,
    /// Name an officer level.
    SetAllegianceOfficerTitle {
        /// The level (1 to 3).
        level: u32,
        /// The title.
        title: String,
    },
    /// Ask for the allegiance name.
    QueryAllegianceName,
    /// Name the allegiance.
    SetAllegianceName(String),
    /// Clear the allegiance name.
    ClearAllegianceName,
    /// A lock action (1 off, 2 on, 3 toggle, 4 check, 5 show the approved
    /// vassal, 6 clear it).
    AllegianceLockAction(u32),
    /// Let one character swear into a locked allegiance.
    SetAllegianceApprovedVassal(String),
    /// An allegiance house action (1 query, 2/3 guest open/close, 4/5
    /// storage open/close).
    AllegianceHouseAction(u32),
    /// Show the message of the day.
    QueryMotd,
    /// Set the message of the day.
    SetMotd(String),
    /// Clear the message of the day.
    ClearMotd,
    /// Open (true) or close the house.
    SetOpenHouseStatus(bool),
    /// Add a permanent guest.
    AddPermanentGuest(String),
    /// Remove a permanent guest.
    RemovePermanentGuest(String),
    /// Remove every permanent guest.
    RemoveAllPermanentGuests,
    /// Grant (true) or take away a player's storage permission.
    ChangeStoragePermission {
        /// The player.
        name: String,
        /// Grant.
        enabled: bool,
    },
    /// Grant every guest storage permission.
    AddAllStoragePermission,
    /// Take away every storage permission.
    RemoveAllStoragePermission,
    /// Ask for the guest list.
    RequestFullGuestList,
    /// Boot one guest from the house.
    BootSpecificHouseGuest(String),
    /// Boot everyone from the house.
    BootEveryone,
    /// Show (true) or hide the house's hooks.
    SetHooksVisibility(bool),
    /// Let (true) or stop the allegiance being guests.
    ModifyAllegianceGuestPermission(bool),
    /// Let (true) or stop the allegiance using storage.
    ModifyAllegianceStoragePermission(bool),
}

/// What the administration commands reach.
pub trait AdministrationHost {
    /// Send a request.
    fn send(&mut self, request: AdministrationRequest);
    /// A line in the transcript.
    fn show_system_message(&mut self, text: &str);
    /// Client-local text.
    fn show_client_local(&mut self, text: &str);
    /// Change one character option.
    fn set_character_option(&mut self, option_id: u32, value: bool);
    /// Ask for a player's allegiance information.
    fn request_allegiance_info(&mut self, name: &str);
}

const SPECIFY_NAME: &str = "Please specify an actual name.";
const SPECIFY_MEMBER: &str = "Please specify the name of an allegiance member.";
const ALLEGIANCE_HELP: &str =
    "Please see @help Allegiance for more information on how to use this command.";
const HOUSE_HELP: &str = "Please see @help House for more information on how to use this command.";

/// Run one administration command; false for a command that is not one.
pub fn execute(
    command: ClientCommandId,
    arguments: &str,
    host: &mut impl AdministrationHost,
) -> bool {
    use AdministrationRequest as R;
    use ClientCommandId as C;
    match command {
        C::AllegianceInfo => {
            let name = arguments.trim();
            if name.is_empty() {
                host.show_client_local(SPECIFY_NAME);
            } else {
                host.request_allegiance_info(name);
            }
        }
        C::AllegianceBoot => allegiance_boot(arguments, host),
        C::AllegianceBan => allegiance_ban(arguments, host),
        C::AllegianceChat => allegiance_chat(arguments, host),
        C::AllegianceBroadcast => {
            let message = arguments.trim();
            if message.is_empty() {
                host.show_client_local(ALLEGIANCE_HELP);
            } else {
                host.send(R::AllegianceBroadcast(message.to_owned()));
            }
        }
        C::AllegianceOfficer => allegiance_officer(arguments, host),
        C::AllegianceOfficerTitle => allegiance_officer_title(arguments, host),
        C::AllegianceName => {
            let operation = first_argument(arguments);
            if operation.is_empty() {
                host.send(R::QueryAllegianceName);
            } else if operation.eq_ignore_ascii_case("set") {
                host.send(R::SetAllegianceName(
                    remainder_after_first_argument(arguments).trim().to_owned(),
                ));
            } else if operation.eq_ignore_ascii_case("clear") {
                host.send(R::ClearAllegianceName);
            } else {
                host.show_client_local(ALLEGIANCE_HELP);
            }
        }
        C::AllegianceLock => allegiance_lock(arguments, host),
        C::AllegianceHouse => allegiance_house(arguments, host),
        C::AllegianceMotd => {
            let operation = first_argument(arguments);
            if operation.is_empty() {
                host.send(R::QueryMotd);
            } else if operation.eq_ignore_ascii_case("set") {
                host.send(R::SetMotd(
                    remainder_after_first_argument(arguments).trim().to_owned(),
                ));
            } else if operation.eq_ignore_ascii_case("clear") {
                host.send(R::ClearMotd);
            } else {
                host.show_client_local(ALLEGIANCE_HELP);
            }
        }
        C::AllegianceUnrecognizedSubcommand => host.show_client_local(ALLEGIANCE_HELP),
        C::HouseOpenStatus => host.send(R::SetOpenHouseStatus(
            arguments.eq_ignore_ascii_case("open"),
        )),
        C::HouseStorage => house_storage(arguments, host),
        C::HouseBoot => {
            let name = arguments.trim();
            if name.is_empty() {
                host.show_client_local(HOUSE_HELP);
            } else if name.eq_ignore_ascii_case("-all") {
                host.send(R::BootEveryone);
            } else {
                host.send(R::BootSpecificHouseGuest(name.to_owned()));
            }
        }
        C::HouseBootAll => host.send(R::BootEveryone),
        C::HouseGuests => house_guests(arguments, host),
        C::HouseHooks => {
            let state = first_argument(arguments);
            if state.eq_ignore_ascii_case("on") {
                host.send(R::SetHooksVisibility(true));
            } else if state.eq_ignore_ascii_case("off") {
                host.send(R::SetHooksVisibility(false));
            } else {
                host.show_client_local(HOUSE_HELP);
            }
        }
        C::HouseUnrecognizedSubcommand => host.show_client_local(HOUSE_HELP),
        _ => return false,
    }
    true
}

fn allegiance_boot(arguments: &str, host: &mut impl AdministrationHost) {
    let mut name = arguments.trim().to_owned();
    if name.is_empty() {
        host.show_client_local(SPECIFY_NAME);
        return;
    }
    let account = match find_ignore_ascii_case(&name, "-account") {
        Some(at) => {
            name.replace_range(at..at + "-account".len(), "");
            name = name.trim().to_owned();
            true
        }
        None => false,
    };
    host.show_system_message(&format!(
        "Attempting to boot {name}{}...",
        if account { " (Account)" } else { "" }
    ));
    host.send(AdministrationRequest::BreakAllegianceBoot { name, account });
}

fn allegiance_ban(arguments: &str, host: &mut impl AdministrationHost) {
    let operation = first_argument(arguments);
    if operation.eq_ignore_ascii_case("list") {
        host.send(AdministrationRequest::ListAllegianceBans);
        return;
    }
    let name = remainder_after_first_argument(arguments).trim();
    if name.is_empty() {
        host.show_client_local(SPECIFY_NAME);
    } else if operation.eq_ignore_ascii_case("add") {
        host.send(AdministrationRequest::AddAllegianceBan(name.to_owned()));
    } else if operation.eq_ignore_ascii_case("remove") {
        host.send(AdministrationRequest::RemoveAllegianceBan(name.to_owned()));
    } else {
        host.show_client_local(ALLEGIANCE_HELP);
    }
}

fn allegiance_chat(arguments: &str, host: &mut impl AdministrationHost) {
    let operation = first_argument(arguments);
    if operation.eq_ignore_ascii_case("on") || operation.eq_ignore_ascii_case("off") {
        host.set_character_option(
            option_id::LISTEN_TO_ALLEGIANCE_CHAT,
            operation.eq_ignore_ascii_case("on"),
        );
        return;
    }
    let remainder = remainder_after_first_argument(arguments).trim();
    if operation.eq_ignore_ascii_case("kick") {
        let (name, reason) = match remainder.find(',') {
            None => (remainder, "No reason given."),
            Some(comma) => (remainder[..comma].trim(), remainder[comma + 1..].trim()),
        };
        host.send(AdministrationRequest::AllegianceChatBoot {
            name: name.to_owned(),
            reason: reason.to_owned(),
        });
        return;
    }
    let gag = operation.eq_ignore_ascii_case("gag");
    if !gag && !operation.eq_ignore_ascii_case("ungag") {
        host.show_client_local(ALLEGIANCE_HELP);
    } else if remainder.is_empty() {
        host.show_client_local(SPECIFY_NAME);
    } else {
        host.send(AdministrationRequest::AllegianceChatGag {
            name: remainder.to_owned(),
            enabled: gag,
        });
    }
}

fn allegiance_officer(arguments: &str, host: &mut impl AdministrationHost) {
    let operation = first_argument(arguments);
    if operation.is_empty() || operation.eq_ignore_ascii_case("list") {
        host.send(AdministrationRequest::ListAllegianceOfficers);
        return;
    }
    if operation.eq_ignore_ascii_case("clear") {
        host.send(AdministrationRequest::ClearAllegianceOfficers);
        return;
    }
    let remainder = remainder_after_first_argument(arguments);
    if operation.eq_ignore_ascii_case("remove") {
        let name = remainder.trim();
        if name.is_empty() {
            host.show_client_local(SPECIFY_MEMBER);
        } else {
            host.send(AdministrationRequest::RemoveAllegianceOfficer(
                name.to_owned(),
            ));
        }
        return;
    }
    if !operation.eq_ignore_ascii_case("add") && !operation.eq_ignore_ascii_case("set") {
        host.show_client_local(ALLEGIANCE_HELP);
        return;
    }
    let Some(level) = officer_level(first_argument(remainder)) else {
        host.show_client_local(
            "Please specify a valid officer level as a number between 1 and 3. \
             Check the game help files for more information on officer levels.",
        );
        return;
    };
    let name = remainder_after_first_argument(remainder).trim();
    if name.is_empty() {
        host.show_client_local(SPECIFY_MEMBER);
    } else {
        host.send(AdministrationRequest::SetAllegianceOfficer {
            name: name.to_owned(),
            level,
        });
    }
}

fn allegiance_officer_title(arguments: &str, host: &mut impl AdministrationHost) {
    let operation = first_argument(arguments);
    if operation.is_empty() || operation.eq_ignore_ascii_case("list") {
        host.send(AdministrationRequest::ListAllegianceOfficerTitles);
        return;
    }
    if operation.eq_ignore_ascii_case("clear") {
        host.send(AdministrationRequest::ClearAllegianceOfficerTitles);
        return;
    }
    if !operation.eq_ignore_ascii_case("set") {
        host.show_client_local(ALLEGIANCE_HELP);
        return;
    }
    let remainder = remainder_after_first_argument(arguments);
    let Some(level) = officer_level(first_argument(remainder)) else {
        host.show_client_local("Please specify a valid officer level as a number between 1 and 3.");
        return;
    };
    host.send(AdministrationRequest::SetAllegianceOfficerTitle {
        level,
        title: remainder_after_first_argument(remainder).trim().to_owned(),
    });
}

fn allegiance_lock(arguments: &str, host: &mut impl AdministrationHost) {
    let operation = first_argument(arguments);
    let action = match operation.to_ascii_lowercase().as_str() {
        "" | "check" => Some(4),
        "off" => Some(1),
        "on" => Some(2),
        "toggle" => Some(3),
        _ => None,
    };
    if let Some(action) = action {
        host.send(AdministrationRequest::AllegianceLockAction(action));
        return;
    }
    if !operation.eq_ignore_ascii_case("bypass") {
        host.show_client_local(ALLEGIANCE_HELP);
        return;
    }
    let approved = remainder_after_first_argument(arguments).trim();
    host.send(if approved.is_empty() {
        AdministrationRequest::AllegianceLockAction(5)
    } else if approved.eq_ignore_ascii_case("clear") {
        AdministrationRequest::AllegianceLockAction(6)
    } else {
        AdministrationRequest::SetAllegianceApprovedVassal(approved.to_owned())
    });
}

fn allegiance_house(arguments: &str, host: &mut impl AdministrationHost) {
    let category = first_argument(arguments).to_ascii_lowercase();
    if category.is_empty() {
        host.send(AdministrationRequest::AllegianceHouseAction(1));
        return;
    }
    let state = first_argument(remainder_after_first_argument(arguments)).to_ascii_lowercase();
    let action = match (category.as_str(), state.as_str()) {
        ("guest", "open") => 2,
        ("guest", "close") => 3,
        ("storage", "open") => 4,
        ("storage", "close") => 5,
        _ => 0,
    };
    if action == 0 {
        host.show_client_local(ALLEGIANCE_HELP);
    } else {
        host.send(AdministrationRequest::AllegianceHouseAction(action));
    }
}

fn house_guests(arguments: &str, host: &mut impl AdministrationHost) {
    use AdministrationRequest as R;
    let operation = first_argument(arguments);
    let name = remainder_after_first_argument(arguments).trim();
    let add = operation.eq_ignore_ascii_case("add");
    if add || operation.eq_ignore_ascii_case("remove") {
        if name.is_empty() {
            host.show_client_local("Please specify the guest's name.");
        } else if add {
            host.send(R::AddPermanentGuest(name.to_owned()));
        } else {
            host.send(R::RemovePermanentGuest(name.to_owned()));
        }
        return;
    }
    if operation.eq_ignore_ascii_case("remove_all") {
        host.send(R::RemoveAllPermanentGuests);
    } else if operation.eq_ignore_ascii_case("list") || operation.eq_ignore_ascii_case("show") {
        host.send(R::RequestFullGuestList);
    } else if operation.eq_ignore_ascii_case("add_allegiance") {
        host.send(R::ModifyAllegianceGuestPermission(true));
    } else if operation.eq_ignore_ascii_case("remove_allegiance") {
        host.send(R::ModifyAllegianceGuestPermission(false));
    } else {
        host.show_client_local(HOUSE_HELP);
    }
}

fn house_storage(arguments: &str, host: &mut impl AdministrationHost) {
    use AdministrationRequest as R;
    let operation = first_argument(arguments);
    let name = remainder_after_first_argument(arguments).trim();
    let enabled = operation.eq_ignore_ascii_case("add");
    if enabled || operation.eq_ignore_ascii_case("remove") {
        if name.is_empty() {
            host.show_client_local(SPECIFY_NAME);
        } else if name.eq_ignore_ascii_case("-all") {
            host.send(if enabled {
                R::AddAllStoragePermission
            } else {
                R::RemoveAllStoragePermission
            });
        } else {
            host.send(R::ChangeStoragePermission {
                name: name.to_owned(),
                enabled,
            });
        }
        return;
    }
    if operation.eq_ignore_ascii_case("remove_all") {
        host.send(R::RemoveAllStoragePermission);
    } else if operation.eq_ignore_ascii_case("list") || operation.eq_ignore_ascii_case("show") {
        host.send(R::RequestFullGuestList);
    } else if operation.eq_ignore_ascii_case("add_allegiance") {
        host.send(R::ModifyAllegianceStoragePermission(true));
    } else if operation.eq_ignore_ascii_case("remove_allegiance") {
        host.send(R::ModifyAllegianceStoragePermission(false));
    } else {
        host.show_client_local(HOUSE_HELP);
    }
}

/// An officer level typed as the C library's `strtol` with base 0 reads
/// it (decimal, `0x` hex, leading-`0` octal; trailing text ignored), kept
/// only when it is 1 to 3.
fn officer_level(text: &str) -> Option<u32> {
    let level = strtol_base_zero(text);
    (1..=3).contains(&level).then(|| level.unsigned_abs())
}

/// `strtol(text, NULL, 0)`, saturating at the `i32` range.
fn strtol_base_zero(text: &str) -> i32 {
    let bytes = text.as_bytes();
    let mut index = 0;
    let mut negative = false;
    if let Some(&sign) = bytes.first()
        && (sign == b'+' || sign == b'-')
    {
        negative = sign == b'-';
        index += 1;
        if index == bytes.len() {
            return 0;
        }
    }
    let mut base = 10;
    if bytes.get(index) == Some(&b'0') {
        base = 8;
        if index + 2 < bytes.len()
            && matches!(bytes[index + 1], b'x' | b'X')
            && hex_digit(bytes[index + 2]).is_some()
        {
            base = 16;
            index += 2;
        }
    }
    let limit = i64::from(i32::MAX) + i64::from(negative);
    let mut result: i64 = 0;
    let mut saw_digit = false;
    while let Some(digit) = bytes.get(index).copied().and_then(hex_digit) {
        if digit >= base {
            break;
        }
        saw_digit = true;
        result = (result * i64::from(base) + i64::from(digit)).min(limit);
        index += 1;
    }
    if !saw_digit {
        return 0;
    }
    let signed = if negative { -result } else { result };
    i32::try_from(signed.clamp(i64::from(i32::MIN), i64::from(i32::MAX))).unwrap_or(0)
}

fn hex_digit(c: u8) -> Option<u32> {
    char::from(c).to_digit(16)
}

fn first_argument(arguments: &str) -> &str {
    let trimmed = arguments.trim();
    trimmed
        .find([' ', '\t', '\r', '\n'])
        .map_or(trimmed, |at| &trimmed[..at])
}

fn remainder_after_first_argument(arguments: &str) -> &str {
    let trimmed = arguments.trim();
    trimmed
        .find([' ', '\t', '\r', '\n'])
        .map_or("", |at| trimmed[at + 1..].trim_start())
}

/// Byte index of `needle` in `haystack`, ignoring ASCII case.
fn find_ignore_ascii_case(haystack: &str, needle: &str) -> Option<usize> {
    let lower = haystack.to_ascii_lowercase();
    lower.find(&needle.to_ascii_lowercase())
}

#[cfg(test)]
mod tests {
    // Golden behaviour: OpenAC tests/AcDream.Runtime.Tests/Chat/RetailAdministrationCommandDispatcherTests.cs.

    use super::*;

    #[derive(Default)]
    struct Calls(Vec<String>);

    impl AdministrationHost for Calls {
        fn send(&mut self, request: AdministrationRequest) {
            use AdministrationRequest as R;
            self.0.push(match request {
                R::SetAllegianceOfficer { name, level } => format!("officer:{level}:{name}"),
                R::AllegianceChatBoot { name, reason } => format!("chatboot:{name}:{reason}"),
                R::AddPermanentGuest(name) => format!("guest:add:{name}"),
                R::RemoveAllStoragePermission => "storage:remove_all".to_owned(),
                R::SetMotd(text) => format!("motd:set:{text}"),
                other => format!("{other:?}"),
            });
        }
        fn show_system_message(&mut self, text: &str) {
            self.0.push(format!("system:{text}"));
        }
        fn show_client_local(&mut self, text: &str) {
            self.0.push(format!("local:{text}"));
        }
        fn set_character_option(&mut self, option_id: u32, value: bool) {
            self.0.push(format!("option:{option_id}:{value}"));
        }
        fn request_allegiance_info(&mut self, name: &str) {
            self.0.push(format!("info:{name}"));
        }
    }

    #[test]
    fn the_whole_administration_family_is_handled_here() {
        use ClientCommandId as C;
        let mut calls = Calls::default();
        for id in [
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
            C::AllegianceUnrecognizedSubcommand,
            C::HouseOpenStatus,
            C::HouseStorage,
            C::HouseBoot,
            C::HouseBootAll,
            C::HouseGuests,
            C::HouseHooks,
            C::HouseUnrecognizedSubcommand,
        ] {
            assert!(execute(id, "", &mut calls), "{id:?}");
        }
        assert!(!execute(C::QueryAge, "", &mut calls));
    }

    #[test]
    fn multi_field_forms_send_their_requests() {
        use ClientCommandId as C;
        let mut calls = Calls::default();
        execute(C::AllegianceOfficer, "add 0x2 Lord Bob", &mut calls);
        execute(C::AllegianceChat, "on", &mut calls);
        execute(C::AllegianceChat, "kick Bob, Be civil", &mut calls);
        execute(C::HouseGuests, "add Lord Bob", &mut calls);
        execute(C::HouseStorage, "remove -all", &mut calls);
        execute(C::AllegianceMotd, "set Welcome home", &mut calls);
        assert_eq!(
            calls.0,
            [
                "officer:2:Lord Bob",
                "option:27:true",
                "chatboot:Bob:Be civil",
                "guest:add:Lord Bob",
                "storage:remove_all",
                "motd:set:Welcome home",
            ]
        );
    }

    #[test]
    fn malformed_forms_answer_in_client_local_text() {
        use ClientCommandId as C;
        let mut calls = Calls::default();
        execute(C::AllegianceOfficer, "add nope Bob", &mut calls);
        execute(C::HouseUnrecognizedSubcommand, "nope", &mut calls);
        assert_eq!(
            calls.0,
            [
                "local:Please specify a valid officer level as a number between 1 and 3. Check the game help files for more information on officer levels.",
                "local:Please see @help House for more information on how to use this command.",
            ]
        );
    }

    #[test]
    fn boot_lock_and_house_forms() {
        use AdministrationRequest as R;
        use ClientCommandId as C;
        let mut calls = Calls::default();
        execute(C::AllegianceBoot, "-ACCOUNT Lord Bob", &mut calls);
        execute(C::AllegianceLock, "", &mut calls);
        execute(C::AllegianceLock, "bypass", &mut calls);
        execute(C::AllegianceLock, "bypass clear", &mut calls);
        execute(C::AllegianceLock, "bypass Bob", &mut calls);
        execute(C::AllegianceHouse, "storage close", &mut calls);
        execute(C::AllegianceChat, "kick Bob", &mut calls);
        execute(C::HouseBoot, "-all", &mut calls);
        execute(C::HouseOpenStatus, "OPEN", &mut calls);
        assert_eq!(
            calls.0,
            [
                "system:Attempting to boot Lord Bob (Account)...".to_owned(),
                format!(
                    "{:?}",
                    R::BreakAllegianceBoot {
                        name: "Lord Bob".into(),
                        account: true
                    }
                ),
                format!("{:?}", R::AllegianceLockAction(4)),
                format!("{:?}", R::AllegianceLockAction(5)),
                format!("{:?}", R::AllegianceLockAction(6)),
                format!("{:?}", R::SetAllegianceApprovedVassal("Bob".into())),
                format!("{:?}", R::AllegianceHouseAction(5)),
                "chatboot:Bob:No reason given.".to_owned(),
                format!("{:?}", R::BootEveryone),
                format!("{:?}", R::SetOpenHouseStatus(true)),
            ]
        );
    }

    #[test]
    fn officer_levels_read_as_strtol_with_base_zero() {
        assert_eq!(strtol_base_zero("3"), 3);
        assert_eq!(strtol_base_zero("0x2"), 2);
        assert_eq!(strtol_base_zero("03"), 3);
        assert_eq!(strtol_base_zero("08"), 0);
        assert_eq!(strtol_base_zero("2abc"), 2);
        assert_eq!(strtol_base_zero("-1"), -1);
        assert_eq!(strtol_base_zero("+"), 0);
        assert_eq!(strtol_base_zero("99999999999"), i32::MAX);
        assert_eq!(strtol_base_zero("-99999999999"), i32::MIN);
        assert_eq!(officer_level("4"), None);
        assert_eq!(officer_level("1"), Some(1));
    }
}
