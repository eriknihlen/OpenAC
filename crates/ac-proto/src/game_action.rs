use ac_bits::{Reader, Writer};

use crate::{Opcode, Result, expect_opcode};

/// The action type inside a [`Opcode::GameAction`] envelope.
///
/// An open set: any `u32` is a valid action type; the constants name the
/// ones this crate builds.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct GameActionType(pub u32);

impl GameActionType {
    /// The client has finished loading after login or a portal trip; until
    /// the server receives it, it ignores the character's actions.
    pub const LOGIN_COMPLETE: Self = Self(0x00A1);
    /// Ask for the character's house status.
    pub const HOUSE_QUERY: Self = Self(0x021E);
}

macro_rules! game_action_types {
    ($($(#[$doc:meta])* $name:ident = $value:literal,)*) => {
        impl GameActionType {
            $($(#[$doc])* pub const $name: Self = Self($value);)*
        }
    };
}

game_action_types! {
    /// Change one character option.
    SET_SINGLE_CHARACTER_OPTION = 0x0005,
    /// Melee attack on a target.
    TARGETED_MELEE_ATTACK = 0x0008,
    /// Missile attack on a target.
    TARGETED_MISSILE_ATTACK = 0x000A,
    /// Turn away-from-keyboard on or off.
    SET_AFK_MODE = 0x000F,
    /// Set the away-from-keyboard reply.
    SET_AFK_MESSAGE = 0x0010,
    /// Say something to everyone nearby.
    TALK = 0x0015,
    /// Remove a friend.
    REMOVE_FRIEND = 0x0017,
    /// Add a friend by name.
    ADD_FRIEND = 0x0018,
    /// Put an item into a container.
    PUT_ITEM_IN_CONTAINER = 0x0019,
    /// Wield an item.
    GET_AND_WIELD_ITEM = 0x001A,
    /// Drop an item on the ground.
    DROP_ITEM = 0x001B,
    /// Swear allegiance to a patron.
    SWEAR_ALLEGIANCE = 0x001D,
    /// Break allegiance with a patron or vassal.
    BREAK_ALLEGIANCE = 0x001E,
    /// Ask for allegiance panel updates on or off.
    ALLEGIANCE_UPDATE_REQUEST = 0x001F,
    /// Clear the friends list.
    CLEAR_FRIENDS = 0x0025,
    /// Recall to the player-killer-lite arena.
    TELE_TO_PK_LITE_ARENA = 0x0026,
    /// Recall to the player-killer arena.
    TELE_TO_PK_ARENA = 0x0027,
    /// Choose the displayed title.
    SET_DISPLAY_CHARACTER_TITLE = 0x002C,
    /// Ask for the allegiance name.
    QUERY_ALLEGIANCE_NAME = 0x0030,
    /// Clear the allegiance name.
    CLEAR_ALLEGIANCE_NAME = 0x0031,
    /// Speak to one object by id.
    TALK_DIRECT = 0x0032,
    /// Set the allegiance name.
    SET_ALLEGIANCE_NAME = 0x0033,
    /// Use one item on another.
    USE_WITH_TARGET = 0x0035,
    /// Use an object.
    USE = 0x0036,
    /// Make a member an allegiance officer.
    SET_ALLEGIANCE_OFFICER = 0x003B,
    /// Name an allegiance officer level.
    SET_ALLEGIANCE_OFFICER_TITLE = 0x003C,
    /// List the allegiance officer titles.
    LIST_ALLEGIANCE_OFFICER_TITLES = 0x003D,
    /// Clear the allegiance officer titles.
    CLEAR_ALLEGIANCE_OFFICER_TITLES = 0x003E,
    /// Lock or unlock the allegiance.
    DO_ALLEGIANCE_LOCK_ACTION = 0x003F,
    /// Pre-approve a vassal for a locked allegiance.
    SET_ALLEGIANCE_APPROVED_VASSAL = 0x0040,
    /// Gag or ungag a member on allegiance chat.
    ALLEGIANCE_CHAT_GAG = 0x0041,
    /// Allegiance house action.
    DO_ALLEGIANCE_HOUSE_ACTION = 0x0042,
    /// Spend experience on a vital.
    RAISE_VITAL = 0x0044,
    /// Spend experience on an attribute.
    RAISE_ATTRIBUTE = 0x0045,
    /// Spend experience on a skill.
    RAISE_SKILL = 0x0046,
    /// Spend credits to train a skill.
    TRAIN_SKILL = 0x0047,
    /// Cast a spell with no target.
    CAST_UNTARGETED_SPELL = 0x0048,
    /// Cast a spell on a target.
    CAST_TARGETED_SPELL = 0x004A,
    /// Change combat mode.
    CHANGE_COMBAT_MODE = 0x0053,
    /// Merge one stack into another.
    STACKABLE_MERGE = 0x0054,
    /// Split part of a stack into a container.
    STACKABLE_SPLIT_TO_CONTAINER = 0x0055,
    /// Split part of a stack onto the ground.
    STACKABLE_SPLIT_TO_3D = 0x0056,
    /// Squelch or unsquelch a character.
    MODIFY_CHARACTER_SQUELCH = 0x0058,
    /// Squelch or unsquelch an account.
    MODIFY_ACCOUNT_SQUELCH = 0x0059,
    /// Squelch or unsquelch a message type globally.
    MODIFY_GLOBAL_SQUELCH = 0x005B,
    /// Tell a player by name.
    TELL = 0x005D,
    /// Buy from a vendor.
    BUY = 0x005F,
    /// Sell to a vendor.
    SELL = 0x0060,
    /// Recall to the lifestone.
    TELE_TO_LIFESTONE = 0x0063,
    /// Create a fellowship.
    FELLOWSHIP_CREATE = 0x00A2,
    /// Quit (or disband) the fellowship.
    FELLOWSHIP_QUIT = 0x00A3,
    /// Dismiss a fellowship member.
    FELLOWSHIP_DISMISS = 0x00A4,
    /// Recruit a player into the fellowship.
    FELLOWSHIP_RECRUIT = 0x00A5,
    /// Fellowship panel opened or closed.
    FELLOWSHIP_UPDATE_REQUEST = 0x00A6,
    /// Ask for a whole book.
    BOOK_DATA = 0x00AA,
    /// Change a book page.
    BOOK_MODIFY_PAGE = 0x00AB,
    /// Add a book page.
    BOOK_ADD_PAGE = 0x00AC,
    /// Delete a book page.
    BOOK_DELETE_PAGE = 0x00AD,
    /// Ask for one book page.
    BOOK_PAGE_DATA = 0x00AE,
    /// Recall to a point of interest.
    TELE_TO_POI = 0x00B1,
    /// Inscribe an item.
    SET_INSCRIPTION = 0x00BF,
    /// Appraise an object.
    IDENTIFY_OBJECT = 0x00C8,
    /// Give an item to another object.
    GIVE_OBJECT_REQUEST = 0x00CD,
    /// Join a chat channel.
    ADD_CHANNEL = 0x0145,
    /// Leave a chat channel.
    REMOVE_CHANNEL = 0x0146,
    /// Say something on a chat channel.
    CHAT_CHANNEL = 0x0147,
    /// List who listens on a channel.
    LIST_CHANNELS = 0x0148,
    /// List the available channels.
    INDEX_CHANNELS = 0x0149,
    /// Stop viewing a container's contents.
    NO_LONGER_VIEWING_CONTENTS = 0x0195,
    /// Split part of a stack into a wield slot.
    STACKABLE_SPLIT_TO_WIELD = 0x019B,
    /// Put an entry on the shortcut bar.
    ADD_SHORTCUT = 0x019C,
    /// Clear a shortcut bar slot.
    REMOVE_SHORTCUT = 0x019D,
    /// Send all character options.
    SET_CHARACTER_OPTIONS = 0x01A1,
    /// Forget a spell.
    REMOVE_SPELL = 0x01A8,
    /// Stop the current attack.
    CANCEL_ATTACK = 0x01B7,
    /// Ask for a target's health.
    QUERY_HEALTH = 0x01BF,
    /// Ask a character's age.
    QUERY_AGE = 0x01C2,
    /// Ask a character's birth date.
    QUERY_BIRTH = 0x01C4,
    /// Emote text.
    EMOTE = 0x01DF,
    /// Soul emote text.
    SOUL_EMOTE = 0x01E1,
    /// Add a spell to a favorites bar.
    ADD_SPELL_FAVORITE = 0x01E3,
    /// Remove a spell from a favorites bar.
    REMOVE_SPELL_FAVORITE = 0x01E4,
    /// Ping the server.
    PING_REQUEST = 0x01E9,
    /// Open trade with a player.
    OPEN_TRADE_NEGOTIATIONS = 0x01F6,
    /// Close the trade window.
    CLOSE_TRADE_NEGOTIATIONS = 0x01F7,
    /// Put an item into the trade.
    ADD_TO_TRADE = 0x01F8,
    /// Accept the trade.
    ACCEPT_TRADE = 0x01FA,
    /// Decline the trade.
    DECLINE_TRADE = 0x01FB,
    /// Clear the trade.
    RESET_TRADE = 0x0204,
    /// Clear the consent list.
    CLEAR_PLAYER_CONSENT_LIST = 0x0216,
    /// Show the consent list.
    DISPLAY_PLAYER_CONSENT_LIST = 0x0217,
    /// Remove a name from the consent list.
    REMOVE_FROM_PLAYER_CONSENT_LIST = 0x0218,
    /// Give a player house access.
    ADD_PLAYER_PERMISSION = 0x0219,
    /// Take house access from a player.
    REMOVE_PLAYER_PERMISSION = 0x021A,
    /// Buy a house at its slumlord with the items offered.
    BUY_HOUSE = 0x021C,
    /// Pay a house's maintenance at its slumlord with the items offered.
    RENT_HOUSE = 0x0221,
    /// Set how many of a spell component to keep.
    SET_DESIRED_COMPONENT_LEVEL = 0x0224,
    /// Abandon the house.
    ABANDON_HOUSE = 0x021F,
    /// Add a permanent house guest.
    ADD_PERMANENT_GUEST = 0x0245,
    /// Remove a permanent house guest.
    REMOVE_PERMANENT_GUEST = 0x0246,
    /// Open or close the house to the public.
    SET_OPEN_HOUSE_STATUS = 0x0247,
    /// Give or take house storage access.
    CHANGE_STORAGE_PERMISSION = 0x0249,
    /// Boot one guest from the house.
    BOOT_SPECIFIC_HOUSE_GUEST = 0x024A,
    /// Take storage access from everyone.
    REMOVE_ALL_STORAGE_PERMISSION = 0x024C,
    /// Ask for the full guest list.
    REQUEST_FULL_GUEST_LIST = 0x024D,
    /// Set the allegiance message of the day.
    SET_MOTD = 0x0254,
    /// Ask for the allegiance message of the day.
    QUERY_MOTD = 0x0255,
    /// Clear the allegiance message of the day.
    CLEAR_MOTD = 0x0256,
    /// Give storage access to every guest.
    ADD_ALL_STORAGE_PERMISSION = 0x025C,
    /// Remove every permanent guest.
    REMOVE_ALL_PERMANENT_GUESTS = 0x025E,
    /// Boot everyone from the house.
    BOOT_EVERYONE = 0x025F,
    /// Recall to the house.
    TELE_TO_HOUSE = 0x0262,
    /// Ask for an item's mana.
    QUERY_ITEM_MANA = 0x0263,
    /// Show or hide house hooks.
    SET_HOOKS_VISIBILITY = 0x0266,
    /// Give or take allegiance guest access.
    MODIFY_ALLEGIANCE_GUEST_PERMISSION = 0x0267,
    /// Give or take allegiance storage access.
    MODIFY_ALLEGIANCE_STORAGE_PERMISSION = 0x0268,
    /// Sit at a game board (chess).
    GAME_JOIN = 0x0269,
    /// Resign the game.
    GAME_QUIT = 0x026A,
    /// Move a piece.
    GAME_MOVE = 0x026B,
    /// Give up the turn.
    GAME_MOVE_PASS = 0x026D,
    /// Offer or withdraw a draw.
    GAME_STALEMATE = 0x026E,
    /// List houses for sale.
    LIST_AVAILABLE_HOUSES = 0x0270,
    /// Answer a confirmation dialog.
    CONFIRMATION_RESPONSE = 0x0275,
    /// Break allegiance with a member and boot them.
    BREAK_ALLEGIANCE_BOOT = 0x0277,
    /// Recall to the allegiance mansion.
    TELE_TO_MANSION = 0x0278,
    /// Die.
    SUICIDE = 0x0279,
    /// Ask for a player's allegiance.
    ALLEGIANCE_INFO_REQUEST = 0x027B,
    /// Salvage items with a tool.
    CREATE_TINKERING_TOOL = 0x027D,
    /// Recall to the marketplace.
    TELE_TO_MARKETPLACE = 0x028D,
    /// Become a player killer lite.
    ENTER_PK_LITE = 0x028F,
    /// Make another member the fellowship leader.
    FELLOWSHIP_ASSIGN_NEW_LEADER = 0x0290,
    /// Open or close the fellowship.
    FELLOWSHIP_CHANGE_OPENNESS = 0x0291,
    /// Set the spellbook filter bits.
    SPELLBOOK_FILTER = 0x0286,
    /// Boot a member from allegiance chat.
    ALLEGIANCE_CHAT_BOOT = 0x02A0,
    /// Ban a player from the allegiance.
    ADD_ALLEGIANCE_BAN = 0x02A1,
    /// Lift an allegiance ban.
    REMOVE_ALLEGIANCE_BAN = 0x02A2,
    /// List the allegiance bans.
    LIST_ALLEGIANCE_BANS = 0x02A3,
    /// Remove an allegiance officer.
    REMOVE_ALLEGIANCE_OFFICER = 0x02A5,
    /// List the allegiance officers.
    LIST_ALLEGIANCE_OFFICERS = 0x02A6,
    /// Clear the allegiance officers.
    CLEAR_ALLEGIANCE_OFFICERS = 0x02A7,
    /// Recall to the allegiance hometown.
    RECALL_ALLEGIANCE_HOMETOWN = 0x02AB,
    /// Abandon a contract (quest).
    ABANDON_CONTRACT = 0x0316,
    /// Jump.
    JUMP = 0xF61B,
    /// The local player's motion state changed.
    MOVE_TO_STATE = 0xF61C,
    /// The local player's position, sent while moving on its own.
    AUTONOMOUS_POSITION = 0xF753,
}

/// The session's game-action counter.
///
/// Every game action except login complete takes the next value: the
/// counter starts at zero for a new session, so the first action carries
/// 1, and it goes back to zero on return to character selection. The value
/// wraps after `u32::MAX`.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct GameActionSequence {
    last: u32,
}

impl GameActionSequence {
    /// A counter for a new session.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Advance and return the number for the next action.
    pub fn advance(&mut self) -> u32 {
        self.last = self.last.wrapping_add(1);
        self.last
    }

    /// The number the last action took (0 before any).
    #[must_use]
    pub fn last(&self) -> u32 {
        self.last
    }

    /// Start over, as on return to character selection.
    pub fn reset(&mut self) {
        self.last = 0;
    }
}

/// Bytes before an action's own arguments: opcode, sequence, action type.
const HEADER_SIZE: usize = 12;

/// A client game action: the [`Opcode::GameAction`] envelope around one
/// action and its arguments.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct GameAction<'a> {
    /// The session's game-action sequence number.
    pub sequence: u32,
    /// Which action.
    pub action_type: GameActionType,
    /// The action's arguments, borrowed from the body.
    pub args: &'a [u8],
}

impl<'a> GameAction<'a> {
    /// Split a game-action body into its envelope fields and arguments.
    pub fn parse(body: &'a [u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::GameAction)?;
        let sequence = r.u32()?;
        let action_type = GameActionType(r.u32()?);
        Ok(Self {
            sequence,
            action_type,
            args: r.peek_rest(),
        })
    }
}

/// Body of a game action: opcode, `sequence`, action type, then `args`.
///
/// The session numbers its actions: each one takes the next value of a
/// counter that is zero for a new session and is reset to zero on return to
/// character selection, so the first numbered action carries 1. Game
/// actions are sent on the UI queue.
#[must_use]
pub fn build_game_action(sequence: u32, action: GameActionType, args: &[u8]) -> Vec<u8> {
    let mut w = Writer::with_capacity(HEADER_SIZE + args.len());
    w.u32(Opcode::GameAction.value());
    w.u32(sequence);
    w.u32(action.0);
    w.bytes(args);
    w.into_vec()
}

/// The login-complete game action. It always carries sequence 0 and does
/// not take a number from the session's action counter.
///
/// Sent on the UI queue once the server has created the player's own object
/// after entering the world (and again after each portal trip).
#[must_use]
pub fn build_login_complete() -> Vec<u8> {
    build_game_action(0, GameActionType::LOGIN_COMPLETE, &[])
}

/// The house-status query game action; no arguments. `sequence` is the next
/// value of the session's action counter.
///
/// Sent on the UI queue right after the first login complete.
#[must_use]
pub fn build_house_query(sequence: u32) -> Vec<u8> {
    build_game_action(sequence, GameActionType::HOUSE_QUERY, &[])
}

impl From<GameAction<'_>> for Vec<u8> {
    fn from(action: GameAction<'_>) -> Self {
        build_game_action(action.sequence, action.action_type, action.args)
    }
}

#[cfg(test)]
mod tests {
    //! Golden layout from OpenAC `tests/AcDream.Core.Net.Tests/Messages/GameActionLoginCompleteTests.cs`
    //! and OpenAC `src/AcDream.Core.Net/Messages/ClientCommandRequests.cs` (`BuildHouseQuery`).

    use super::*;
    use crate::ProtoError;
    use crate::test_util::{assert_every_prefix_fails, words};

    #[test]
    fn login_complete_is_twelve_bytes_with_sequence_zero() {
        assert_eq!(
            build_login_complete(),
            [
                0xB1, 0xF7, 0x00, 0x00, // opcode 0xF7B1
                0x00, 0x00, 0x00, 0x00, // sequence 0
                0xA1, 0x00, 0x00, 0x00, // action 0xA1
            ]
        );
    }

    #[test]
    fn house_query_carries_its_sequence() {
        assert_eq!(build_house_query(1), words(&[0xF7B1, 1, 0x021E]));
    }

    #[test]
    fn arguments_follow_the_header() {
        let body = build_game_action(7, GameActionType(0x1234), &[1, 2, 3, 4]);
        assert_eq!(body[..12], words(&[0xF7B1, 7, 0x1234]));
        assert_eq!(body[12..], [1, 2, 3, 4]);
        let parsed = GameAction::parse(&body).unwrap();
        assert_eq!(parsed.sequence, 7);
        assert_eq!(parsed.action_type, GameActionType(0x1234));
        assert_eq!(parsed.args, [1, 2, 3, 4]);
        assert_eq!(Vec::from(parsed), body);
    }

    #[test]
    fn sequence_starts_at_one_and_resets_to_zero() {
        // OpenAC `WorldSession.NextGameActionSequence` (`++_gameActionSequence`)
        // and `ReturnToCharacterSelect` (`_gameActionSequence = 0`).
        let mut seq = GameActionSequence::new();
        assert_eq!(seq.last(), 0);
        assert_eq!(seq.advance(), 1);
        assert_eq!(seq.advance(), 2);
        seq.reset();
        assert_eq!(seq.advance(), 1);
        let mut wrap = GameActionSequence { last: u32::MAX };
        assert_eq!(wrap.advance(), 0);
    }

    #[test]
    fn parse_rejects_wrong_opcode_and_truncation() {
        assert!(matches!(
            GameAction::parse(&words(&[0xF7B0, 0, 0xA1])),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert_every_prefix_fails(&build_login_complete(), |b| {
            GameAction::parse(b).map(|a| a.sequence)
        });
    }
}
