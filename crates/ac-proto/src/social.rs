//! Social state: friends, squelches, titles, character options, the
//! confirmation dialogs and the ping round trip.
//!
//! Game-action builders take the session's next action sequence number
//! and are sent on the UI queue unless their documentation says otherwise.

use ac_bits::{Reader, Writer};

use crate::wire::{action, bare_action, read_bool32, read_string, string_action, u32_action};
use crate::{DesiredComponent, GameActionType, Opcode, ProtoError, Result, Shortcut};

/// Largest friends list the parser accepts.
pub const MAX_FRIENDS: u32 = 65_536;

/// Largest id list inside one friend entry.
pub const MAX_FRIEND_LINKS: u32 = 10_000;

/// Largest title list the parser accepts.
pub const MAX_TITLES: u32 = 65_536;

/// Largest squelch message-type bit set, in 32-bit words.
pub const MAX_SQUELCH_WORDS: u32 = 4;

/// Ask the server to answer with a ping response.
#[must_use]
pub fn build_ping_request(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::PING_REQUEST)
}

/// Check a ping response ([`crate::GameEventType::PING_RESPONSE`]): it has
/// no payload; receiving it is the answer.
pub fn parse_ping_response(payload: &[u8]) -> Result<()> {
    if payload.is_empty() {
        Ok(())
    } else {
        Err(ProtoError::InvalidLength {
            what: "ping response",
            actual: payload.len(),
        })
    }
}

/// Choose the displayed title.
#[must_use]
pub fn build_set_display_title(sequence: u32, title_id: u32) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::SET_DISPLAY_CHARACTER_TITLE,
        title_id,
    )
}

/// The titles the character owns ([`crate::GameEventType::CHARACTER_TITLE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CharacterTitleTable {
    /// The displayed title, 0 for none.
    pub display_title_id: u32,
    /// Every owned title, in wire order.
    pub title_ids: Vec<u32>,
}

impl CharacterTitleTable {
    /// Parse the event payload: a leading word (not used), the displayed
    /// title, the count, then the titles.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let _tag = r.u32()?;
        let display_title_id = r.u32()?;
        let count = r.u32()?;
        if count > MAX_TITLES {
            return Err(ProtoError::CountTooLarge {
                what: "titles",
                count,
                max: MAX_TITLES,
            });
        }
        let title_ids = (0..count)
            .map(|_| r.u32())
            .collect::<ac_bits::Result<_>>()?;
        Ok(Self {
            display_title_id,
            title_ids,
        })
    }
}

/// A title gained ([`crate::GameEventType::UPDATE_TITLE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct UpdateTitle {
    /// The title.
    pub title_id: u32,
    /// True when it becomes the displayed title.
    pub set_as_display: bool,
}

impl UpdateTitle {
    /// Parse the event payload: title, display flag word.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            title_id: r.u32()?,
            set_as_display: read_bool32(&mut r)?,
        })
    }
}

/// Change one character option (a `u32` 1 or 0).
#[must_use]
pub fn build_set_single_character_option(sequence: u32, option_id: u32, value: bool) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::SET_SINGLE_CHARACTER_OPTION);
    w.u32(option_id);
    w.u32(u32::from(value));
    w.into_vec()
}

/// Header bits of the full options block: eight favorite-spell lists,
/// spellbook filters and the second option word are always present.
const OPTIONS_HEADER_BASE: u32 = 0x400 | 0x020 | 0x040;
/// Header bit: a shortcut list follows.
const OPTIONS_HEADER_SHORTCUTS: u32 = 0x001;
/// Header bit: a desired-component list follows.
const OPTIONS_HEADER_DESIRED_COMPONENTS: u32 = 0x008;

/// Every character option, as the full options action sends them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CharacterOptions<'a> {
    /// First option word.
    pub options1: u32,
    /// Second option word.
    pub options2: u32,
    /// The shortcut bar.
    pub shortcuts: &'a [Shortcut],
    /// The eight favorite-spell bars, spell ids in order.
    pub favorite_spells: [&'a [u32]; 8],
    /// Desired spell components.
    pub desired_components: &'a [DesiredComponent],
    /// Spellbook filter bits.
    pub spellbook_filters: u32,
}

/// Send every character option. The block has the layout of the player
/// description's option trailer: header bits, first option word, the
/// shortcut list when not empty, the eight favorite lists, the desired
/// components when not empty, the spellbook filters, the second option
/// word.
#[must_use]
pub fn build_set_character_options(sequence: u32, options: &CharacterOptions<'_>) -> Vec<u8> {
    let mut header = OPTIONS_HEADER_BASE;
    if !options.shortcuts.is_empty() {
        header |= OPTIONS_HEADER_SHORTCUTS;
    }
    if !options.desired_components.is_empty() {
        header |= OPTIONS_HEADER_DESIRED_COMPONENTS;
    }
    let mut w = action(sequence, GameActionType::SET_CHARACTER_OPTIONS);
    w.u32(header);
    w.u32(options.options1);
    if !options.shortcuts.is_empty() {
        w.u32(options.shortcuts.len() as u32);
        for shortcut in options.shortcuts {
            w.i32(shortcut.index);
            w.u32(shortcut.object_id);
            w.u32(shortcut.spell_id);
        }
    }
    for list in options.favorite_spells {
        w.u32(list.len() as u32);
        for &spell in list {
            w.u32(spell);
        }
    }
    if !options.desired_components.is_empty() {
        w.u32(options.desired_components.len() as u32);
        for component in options.desired_components {
            w.u32(component.id);
            w.u32(component.amount);
        }
    }
    w.u32(options.spellbook_filters);
    w.u32(options.options2);
    w.align(4);
    w.into_vec()
}

/// One entry of the friends list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FriendEntry {
    /// The friend's character id.
    pub id: u32,
    /// Online now.
    pub online: bool,
    /// The friend chose to appear offline.
    pub appear_offline: bool,
    /// The friend's name.
    pub name: String,
    /// Who the friend has on their list.
    pub friends: Vec<u32>,
    /// Who has the friend on their list.
    pub friend_of: Vec<u32>,
}

/// A friends-list change ([`crate::GameEventType::FRIENDS_LIST_UPDATE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FriendsUpdate {
    /// The entries, in wire order.
    pub entries: Vec<FriendEntry>,
    /// What to do with them: 0 full list, 1 add, 2 remove, 3 remove
    /// silently, 4 online status.
    pub update_type: u32,
}

impl FriendsUpdate {
    /// Parse the event payload: count, the entries, then the update type.
    /// An update type outside 0..=4 is an error.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let count = r.u32()?;
        if count > MAX_FRIENDS {
            return Err(ProtoError::CountTooLarge {
                what: "friends",
                count,
                max: MAX_FRIENDS,
            });
        }
        let mut entries = Vec::new();
        for _ in 0..count {
            entries.push(FriendEntry {
                id: r.u32()?,
                online: read_bool32(&mut r)?,
                appear_offline: read_bool32(&mut r)?,
                name: read_string(&mut r)?,
                friends: read_id_list(&mut r)?,
                friend_of: read_id_list(&mut r)?,
            });
        }
        let update_type = r.u32()?;
        if update_type > 4 {
            return Err(ProtoError::InvalidValue {
                what: "friends update type",
                value: update_type,
            });
        }
        Ok(Self {
            entries,
            update_type,
        })
    }
}

fn read_id_list(r: &mut Reader<'_>) -> Result<Vec<u32>> {
    let count = r.u32()?;
    if count > MAX_FRIEND_LINKS {
        return Err(ProtoError::CountTooLarge {
            what: "friend links",
            count,
            max: MAX_FRIEND_LINKS,
        });
    }
    Ok((0..count)
        .map(|_| r.u32())
        .collect::<ac_bits::Result<_>>()?)
}

/// Add a friend by name.
pub fn build_add_friend(sequence: u32, name: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::ADD_FRIEND, name)
}

/// Remove a friend by character id.
#[must_use]
pub fn build_remove_friend(sequence: u32, friend_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::REMOVE_FRIEND, friend_id)
}

/// Clear the friends list.
#[must_use]
pub fn build_clear_friends(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::CLEAR_FRIENDS)
}

/// A legacy friends-list command ([`Opcode::FriendsOld`]): a plain message,
/// not a game action, sent on the control queue.
pub fn build_legacy_friends_command(command: u32, name: &str) -> Result<Vec<u8>> {
    let mut w = Writer::new();
    w.u32(Opcode::FriendsOld.value());
    w.u32(command);
    w.string16l(name)?;
    Ok(w.into_vec())
}

/// The legacy friends-list request the session sends: command 0, no name.
#[must_use]
pub fn build_legacy_friends_list_request() -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(Opcode::FriendsOld.value());
    w.u32(0);
    w.u32(0);
    w.into_vec()
}

/// Squelch (`add`) or unsquelch a character's messages of one type.
/// Fields: add flag, character id, name, message type.
pub fn build_modify_character_squelch(
    sequence: u32,
    add: bool,
    character_id: u32,
    name: &str,
    message_type: u32,
) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::MODIFY_CHARACTER_SQUELCH);
    w.u32(u32::from(add));
    w.u32(character_id);
    w.string16l(name)?;
    w.u32(message_type);
    Ok(w.into_vec())
}

/// Squelch or unsquelch an account by character name.
pub fn build_modify_account_squelch(sequence: u32, add: bool, name: &str) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::MODIFY_ACCOUNT_SQUELCH);
    w.u32(u32::from(add));
    w.string16l(name)?;
    Ok(w.into_vec())
}

/// Squelch or unsquelch one message type from everyone.
#[must_use]
pub fn build_modify_global_squelch(sequence: u32, add: bool, message_type: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::MODIFY_GLOBAL_SQUELCH);
    w.u32(u32::from(add));
    w.u32(message_type);
    w.into_vec()
}

/// One squelch: whose, how wide, and which message types.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SquelchInfo {
    /// The squelched name (empty for the global entry).
    pub name: String,
    /// The squelch covers the whole account.
    pub account_wide: bool,
    /// Squelched message types, ascending.
    pub message_types: Vec<u32>,
}

impl SquelchInfo {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        let word_count = r.u32()?;
        if word_count > MAX_SQUELCH_WORDS {
            return Err(ProtoError::CountTooLarge {
                what: "squelch type words",
                count: word_count,
                max: MAX_SQUELCH_WORDS,
            });
        }
        let mut message_types = Vec::new();
        for word in 0..word_count {
            let bits = r.u32()?;
            message_types.extend(
                (0..32)
                    .filter(|bit| bits & (1 << bit) != 0)
                    .map(|bit| word * 32 + bit),
            );
        }
        Ok(Self {
            name: read_string(r)?,
            account_wide: read_bool32(r)?,
            message_types,
        })
    }
}

/// The player's squelches ([`crate::GameEventType::SET_SQUELCH_DB`]).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SquelchDatabase {
    /// Squelched accounts: name and account id, in wire order.
    pub accounts: Vec<(String, u32)>,
    /// Squelched characters: id and squelch, in wire order.
    pub characters: Vec<(u32, SquelchInfo)>,
    /// Message types squelched from everyone.
    pub global: SquelchInfo,
}

impl SquelchDatabase {
    /// Parse the event payload: the account table, the character table,
    /// then the global entry. A table header is a 16-bit count and a 16-bit
    /// bucket count; a non-empty table with no buckets is an error.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let count = read_hash_header(&mut r, "squelched accounts")?;
        let accounts = (0..count)
            .map(|_| Ok((read_string(&mut r)?, r.u32()?)))
            .collect::<Result<_>>()?;
        let count = read_hash_header(&mut r, "squelched characters")?;
        let characters = (0..count)
            .map(|_| Ok((r.u32()?, SquelchInfo::read(&mut r)?)))
            .collect::<Result<_>>()?;
        Ok(Self {
            accounts,
            characters,
            global: SquelchInfo::read(&mut r)?,
        })
    }
}

/// A hash-table header: count in the low 16 bits, buckets in the high 16.
/// Returns the count; a non-empty table with no buckets is an error.
pub(crate) fn read_hash_header(r: &mut Reader<'_>, what: &'static str) -> Result<u16> {
    let header = r.u32()?;
    let count = header as u16;
    let buckets = (header >> 16) as u16;
    if buckets == 0 && count != 0 {
        return Err(ProtoError::Inconsistent { what });
    }
    Ok(count)
}

/// The confirmation dialog kinds.
pub mod confirmation_type {
    /// Swear allegiance.
    pub const SWEAR_ALLEGIANCE: u32 = 1;
    /// Change a skill.
    pub const ALTER_SKILL: u32 = 2;
    /// Change an attribute.
    pub const ALTER_ATTRIBUTE: u32 = 3;
    /// Join a fellowship.
    pub const FELLOWSHIP: u32 = 4;
    /// Crafting.
    pub const CRAFT_INTERACTION: u32 = 5;
    /// Augmentation.
    pub const AUGMENTATION: u32 = 6;
    /// A yes/no question.
    pub const YES_NO: u32 = 7;
}

/// The server asks the player to confirm something
/// ([`crate::GameEventType::CHARACTER_CONFIRMATION_REQUEST`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConfirmationRequest {
    /// Dialog kind (see [`confirmation_type`]).
    pub confirmation_type: u32,
    /// The server's id for this question; the answer echoes it.
    pub context_id: u32,
    /// The question.
    pub message: String,
}

impl ConfirmationRequest {
    /// Parse the event payload: kind, context id, message.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            confirmation_type: r.u32()?,
            context_id: r.u32()?,
            message: read_string(&mut r)?,
        })
    }
}

/// The server closes a confirmation dialog
/// ([`crate::GameEventType::CHARACTER_CONFIRMATION_DONE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ConfirmationDone {
    /// Dialog kind.
    pub confirmation_type: u32,
    /// The question it closes.
    pub context_id: u32,
}

impl ConfirmationDone {
    /// Parse the event payload: kind, context id.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            confirmation_type: r.u32()?,
            context_id: r.u32()?,
        })
    }
}

/// The player's answer to a confirmation dialog.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ConfirmationResponse {
    /// Dialog kind.
    pub confirmation_type: u32,
    /// The question it answers.
    pub context_id: u32,
    /// Yes.
    pub accepted: bool,
}

impl ConfirmationResponse {
    /// Parse the action's arguments: kind, context id, accepted word.
    pub fn parse(args: &[u8]) -> Result<Self> {
        let mut r = Reader::new(args);
        Ok(Self {
            confirmation_type: r.u32()?,
            context_id: r.u32()?,
            accepted: read_bool32(&mut r)?,
        })
    }
}

/// Answer a confirmation dialog: kind, context id, then 1 or 0.
#[must_use]
pub fn build_confirmation_response(
    sequence: u32,
    confirmation_type: u32,
    context_id: u32,
    accepted: bool,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::CONFIRMATION_RESPONSE);
    w.u32(confirmation_type);
    w.u32(context_id);
    w.u32(u32::from(accepted));
    w.into_vec()
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `SocialActionsTests.cs`, `SocialStateMessagesTests.cs`,
    //! `CharacterTitleEventsTests.cs`, `ConfirmationTripleTests.cs` and
    //! `ClientCommandRequestsTests.cs` (friends, squelch, confirmation,
    //! legacy friends command).

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    #[test]
    fn ping_request_and_response() {
        assert_eq!(build_ping_request(1), words(&[0xF7B1, 1, 0x01E9]));
        assert_eq!(parse_ping_response(&[]), Ok(()));
        assert!(parse_ping_response(&[0]).is_err());
    }

    #[test]
    fn title_set_golden_bytes() {
        assert_eq!(
            build_set_display_title(7, 13),
            [
                0xB1, 0xF7, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x0D, 0x00,
                0x00, 0x00,
            ]
        );
    }

    #[test]
    fn title_table_discards_its_leading_word() {
        let table = CharacterTitleTable::parse(&words(&[1, 13, 3, 1, 5, 13])).unwrap();
        assert_eq!(table.display_title_id, 13);
        assert_eq!(table.title_ids, [1, 5, 13]);
        let table = CharacterTitleTable::parse(&words(&[0xDEAD_BEEF, 7, 0])).unwrap();
        assert_eq!(table.display_title_id, 7);
        assert!(table.title_ids.is_empty());
        for len in [0, 4, 8] {
            assert!(CharacterTitleTable::parse(&vec![0; len]).is_err());
        }
        assert!(CharacterTitleTable::parse(&words(&[1, 1, 2, 1])).is_err());
        assert!(CharacterTitleTable::parse(&words(&[1, 1, 70_000])).is_err());
        assert_every_prefix_fails(&words(&[1, 13, 2, 1, 5]), CharacterTitleTable::parse);
    }

    #[test]
    fn update_title() {
        assert_eq!(
            UpdateTitle::parse(&words(&[13, 1])),
            Ok(UpdateTitle {
                title_id: 13,
                set_as_display: true
            })
        );
        assert!(!UpdateTitle::parse(&words(&[0, 0])).unwrap().set_as_display);
        assert!(
            UpdateTitle::parse(&words(&[5, 0xFF]))
                .unwrap()
                .set_as_display
        );
        assert_every_prefix_fails(&words(&[5, 1]), UpdateTitle::parse);
    }

    #[test]
    fn single_character_option() {
        assert_eq!(
            build_set_single_character_option(1, 0x26, true),
            words(&[0xF7B1, 1, 0x05, 0x26, 1])
        );
        assert_eq!(
            build_set_single_character_option(2, 0x23, false),
            words(&[0xF7B1, 2, 0x05, 0x23, 0])
        );
    }

    #[test]
    fn character_options_golden_bytes() {
        let shortcuts = [Shortcut {
            index: 0,
            object_id: 0x8000_0001,
            spell_id: 0,
        }];
        let first_tab = [1234];
        let components = [DesiredComponent {
            id: 0x6800_0001,
            amount: 12,
        }];
        let body = build_set_character_options(
            5,
            &CharacterOptions {
                options1: 0x50C4_A54A,
                options2: 0x0094_8700,
                shortcuts: &shortcuts,
                favorite_spells: [&first_tab, &[], &[], &[], &[], &[], &[], &[]],
                desired_components: &components,
                spellbook_filters: 0x3FFF,
            },
        );
        let expected = words(&[
            0xF7B1,
            5,
            0x01A1,
            0x469,
            0x50C4_A54A,
            1,
            0,
            0x8000_0001,
            0,
            1,
            1234,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            1,
            0x6800_0001,
            12,
            0x3FFF,
            0x0094_8700,
        ]);
        assert_eq!(body, expected);
        assert_eq!(body.len(), 92);
    }

    #[test]
    fn character_options_omit_empty_sections() {
        let body = build_set_character_options(
            1,
            &CharacterOptions {
                options1: 0,
                options2: 0,
                shortcuts: &[],
                favorite_spells: [&[][..]; 8],
                desired_components: &[],
                spellbook_filters: 0,
            },
        );
        assert_eq!(body[12..16], 0x460u32.to_le_bytes());
        assert_eq!(body.len(), 12 + 8 + 32 + 8);
    }

    #[test]
    fn character_options_round_trip_through_the_player_description_trailer() {
        let shortcuts = [
            Shortcut {
                index: 3,
                object_id: 0x7000_0010,
                spell_id: 0,
            },
            Shortcut {
                index: 4,
                object_id: 0x7000_0011,
                spell_id: 5,
            },
        ];
        let first_tab = [111, 222];
        let components = [
            DesiredComponent {
                id: 0x6800_0002,
                amount: 3,
            },
            DesiredComponent {
                id: 0x6800_0003,
                amount: 7,
            },
        ];
        let body = build_set_character_options(
            9,
            &CharacterOptions {
                options1: 0x1234_5678,
                options2: 0x0000_ABCD,
                shortcuts: &shortcuts,
                favorite_spells: [&first_tab, &[], &[], &[], &[], &[], &[], &[]],
                desired_components: &components,
                spellbook_filters: 0x1234,
            },
        );
        // A description with no property or vector sections, then the
        // options block as its trailer.
        let mut description = vec![0u8; 16];
        description.extend_from_slice(&body[12..]);
        let parsed = crate::PlayerDescription::parse(&description).unwrap();
        assert!(!parsed.trailer_truncated);
        assert_eq!(parsed.options1, 0x1234_5678);
        assert_eq!(parsed.options2, 0x0000_ABCD);
        assert_eq!(parsed.spellbook_filters, 0x1234);
        assert_eq!(parsed.shortcuts, shortcuts);
        assert_eq!(parsed.hotbar_spells.len(), 8);
        assert_eq!(parsed.hotbar_spells[0], [111, 222]);
        assert!(parsed.hotbar_spells[1..].iter().all(Vec::is_empty));
        assert_eq!(parsed.desired_comps, components);
    }

    fn friend(id: u32, online: bool, offline: bool, name: &str, a: &[u32], b: &[u32]) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(id);
        w.u32(u32::from(online));
        w.u32(u32::from(offline));
        w.string16l(name).unwrap();
        w.u32(a.len() as u32);
        for &x in a {
            w.u32(x);
        }
        w.u32(b.len() as u32);
        for &x in b {
            w.u32(x);
        }
        w.into_vec()
    }

    #[test]
    fn friends_update_reads_entries_then_type() {
        let mut payload = words(&[2]);
        payload.extend(friend(
            0x5000_0001,
            true,
            false,
            "Alice",
            &[0x5000_0002],
            &[0x5000_0003, 0x5000_0004],
        ));
        payload.extend(friend(0x5000_0002, false, true, "Bjørn", &[], &[]));
        payload.extend(words(&[0]));
        let update = FriendsUpdate::parse(&payload).unwrap();
        assert_eq!(update.update_type, 0);
        assert_eq!(update.entries.len(), 2);
        let alice = &update.entries[0];
        assert_eq!(alice.id, 0x5000_0001);
        assert_eq!(alice.name, "Alice");
        assert!(alice.online && !alice.appear_offline);
        assert_eq!(alice.friends, [0x5000_0002]);
        assert_eq!(alice.friend_of, [0x5000_0003, 0x5000_0004]);
        let bjorn = &update.entries[1];
        assert_eq!(bjorn.name, "Bjørn");
        assert!(!bjorn.online && bjorn.appear_offline);
        assert_every_prefix_fails(&payload, FriendsUpdate::parse);
        assert!(FriendsUpdate::parse(&words(&[1, 0x5000_0001])).is_err());
        let mut bad_type = words(&[0]);
        bad_type.extend(words(&[5]));
        assert!(matches!(
            FriendsUpdate::parse(&bad_type),
            Err(ProtoError::InvalidValue { value: 5, .. })
        ));
    }

    fn squelch_info(w: &mut Writer, name: &str, account_wide: bool, types: &[u32]) {
        let words_needed = types.iter().max().map_or(0, |max| max / 32 + 1);
        let mut bits = vec![0u32; words_needed as usize];
        for &t in types {
            bits[(t / 32) as usize] |= 1 << (t % 32);
        }
        w.u32(bits.len() as u32);
        for b in bits {
            w.u32(b);
        }
        w.string16l(name).unwrap();
        w.u32(u32::from(account_wide));
    }

    #[test]
    fn squelch_database_reads_tables_and_bit_sets() {
        let mut w = Writer::new();
        w.u32((128 << 16) | 1);
        w.string16l("MutedAccount").unwrap();
        w.u32(0x1122_3344);
        w.u32((128 << 16) | 1);
        w.u32(0x5000_0001);
        squelch_info(&mut w, "Muted Character", true, &[2, 17]);
        squelch_info(&mut w, "", false, &[7, 19, 40]);
        let db = SquelchDatabase::parse(w.as_slice()).unwrap();
        assert_eq!(db.accounts, [("MutedAccount".to_string(), 0x1122_3344)]);
        let (id, character) = &db.characters[0];
        assert_eq!(*id, 0x5000_0001);
        assert_eq!(character.name, "Muted Character");
        assert!(character.account_wide);
        assert_eq!(character.message_types, [2, 17]);
        assert!(!db.global.account_wide);
        assert_eq!(db.global.message_types, [7, 19, 40]);
        assert_every_prefix_fails(w.as_slice(), SquelchDatabase::parse);
        assert!(matches!(
            SquelchDatabase::parse(&words(&[1])),
            Err(ProtoError::Inconsistent { .. })
        ));
        assert!(SquelchDatabase::parse(&words(&[0, 0, 5])).is_err());
    }

    #[test]
    fn friend_and_squelch_requests() {
        assert_eq!(
            build_remove_friend(2, 0x5000_0001),
            words(&[0xF7B1, 2, 0x17, 0x5000_0001])
        );
        assert_eq!(build_clear_friends(2), words(&[0xF7B1, 2, 0x25]));
        let add = build_add_friend(11, "Bjørn").unwrap();
        assert_eq!(add[8..12], 0x18u32.to_le_bytes());
        assert_eq!(add[12..], [5, 0, 0x42, 0x6A, 0xF8, 0x72, 0x6E, 0]);
        assert_eq!(
            build_modify_global_squelch(3, false, 17),
            words(&[0xF7B1, 3, 0x5B, 0, 17])
        );
        let character = build_modify_character_squelch(4, true, 0x5000_0001, "Alice", 12).unwrap();
        assert_eq!(character.len(), 32);
        assert_eq!(character[..20], words(&[0xF7B1, 4, 0x58, 1, 0x5000_0001]));
        assert_eq!(character[20..22], [5, 0]);
        assert_eq!(character[28..], 12u32.to_le_bytes());
        let account = build_modify_account_squelch(5, false, "Alice").unwrap();
        assert_eq!(account.len(), 24);
        assert_eq!(account[..16], words(&[0xF7B1, 5, 0x59, 0]));
        assert_eq!(account[16..18], [5, 0]);
        // A plain message on the control queue, not a game action.
        assert_eq!(build_legacy_friends_list_request(), words(&[0xF7CD, 0, 0]));
        assert_eq!(
            build_legacy_friends_command(0, "").unwrap(),
            build_legacy_friends_list_request()
        );
    }

    #[test]
    fn confirmation_round_trip() {
        let body = build_confirmation_response(9, 7, 42, true);
        assert_eq!(body, words(&[0xF7B1, 9, 0x0275, 7, 42, 1]));
        let parsed =
            ConfirmationResponse::parse(&build_confirmation_response(5, 4, 0x1234, true)[12..])
                .unwrap();
        assert_eq!(parsed.confirmation_type, confirmation_type::FELLOWSHIP);
        assert_eq!(parsed.context_id, 0x1234);
        assert!(parsed.accepted);
        let declined = ConfirmationResponse::parse(&words(&[1, 0x99, 0])).unwrap();
        assert_eq!(
            declined.confirmation_type,
            confirmation_type::SWEAR_ALLEGIANCE
        );
        assert!(!declined.accepted);
        assert!(ConfirmationResponse::parse(&[0; 8]).is_err());

        let mut w = Writer::new();
        w.u32(confirmation_type::YES_NO);
        w.u32(77);
        w.string16l("Are you sure?").unwrap();
        let request = ConfirmationRequest::parse(w.as_slice()).unwrap();
        assert_eq!(request.context_id, 77);
        assert_eq!(request.message, "Are you sure?");
        assert!(ConfirmationRequest::parse(&words(&[7])).is_err());
        assert_eq!(
            ConfirmationDone::parse(&words(&[7, 77])),
            Ok(ConfirmationDone {
                confirmation_type: 7,
                context_id: 77
            })
        );
        assert_every_prefix_fails(&words(&[7, 77]), ConfirmationDone::parse);
    }
}
