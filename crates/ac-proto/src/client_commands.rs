//! Slash-command requests that have no subject module of their own:
//! recalls, player-killer-lite, suicide, age and birth queries, away from
//! keyboard, the consent list and chat channel membership.
//!
//! Every builder here is a game action sent on the UI queue; `sequence` is
//! the next value of the session's [`crate::GameActionSequence`].

use crate::wire::{action, bare_action, string_action, u32_action};
use crate::{GameActionType, Result};

/// Recall to the lifestone.
#[must_use]
pub fn build_tele_to_lifestone(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::TELE_TO_LIFESTONE)
}

/// Recall to the marketplace.
#[must_use]
pub fn build_tele_to_marketplace(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::TELE_TO_MARKETPLACE)
}

/// Recall to the player-killer arena.
#[must_use]
pub fn build_tele_to_pk_arena(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::TELE_TO_PK_ARENA)
}

/// Recall to the player-killer-lite arena.
#[must_use]
pub fn build_tele_to_pk_lite_arena(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::TELE_TO_PK_LITE_ARENA)
}

/// Recall to the house.
#[must_use]
pub fn build_tele_to_house(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::TELE_TO_HOUSE)
}

/// Recall to the allegiance mansion.
#[must_use]
pub fn build_tele_to_mansion(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::TELE_TO_MANSION)
}

/// Recall to the allegiance hometown.
#[must_use]
pub fn build_recall_allegiance_hometown(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::RECALL_ALLEGIANCE_HOMETOWN)
}

/// Recall to a point of interest (a quest-driven recall).
#[must_use]
pub fn build_tele_to_poi(sequence: u32, poi_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::TELE_TO_POI, poi_id)
}

/// Become a player killer lite.
#[must_use]
pub fn build_enter_pk_lite(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::ENTER_PK_LITE)
}

/// Die on purpose.
#[must_use]
pub fn build_suicide(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::SUICIDE)
}

/// Ask a character's age; `object_id` 0 asks about the player.
#[must_use]
pub fn build_query_age(sequence: u32, object_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::QUERY_AGE, object_id)
}

/// Ask a character's birth date; `object_id` 0 asks about the player.
#[must_use]
pub fn build_query_birth(sequence: u32, object_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::QUERY_BIRTH, object_id)
}

/// Turn away-from-keyboard on or off (a `u32` 1 or 0).
#[must_use]
pub fn build_set_afk_mode(sequence: u32, away: bool) -> Vec<u8> {
    u32_action(sequence, GameActionType::SET_AFK_MODE, u32::from(away))
}

/// Set the away-from-keyboard reply.
pub fn build_set_afk_message(sequence: u32, message: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::SET_AFK_MESSAGE, message)
}

/// Clear the consent list.
#[must_use]
pub fn build_clear_consent(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::CLEAR_PLAYER_CONSENT_LIST)
}

/// Show the consent list.
#[must_use]
pub fn build_display_consent(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::DISPLAY_PLAYER_CONSENT_LIST)
}

/// Remove a name from the consent list.
pub fn build_remove_consent(sequence: u32, name: &str) -> Result<Vec<u8>> {
    string_action(
        sequence,
        GameActionType::REMOVE_FROM_PLAYER_CONSENT_LIST,
        name,
    )
}

/// List the chat channels available to the player.
#[must_use]
pub fn build_index_channels(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::INDEX_CHANNELS)
}

/// List who listens on a channel.
#[must_use]
pub fn build_list_channel(sequence: u32, channel_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::LIST_CHANNELS, channel_id)
}

/// Join a chat channel.
#[must_use]
pub fn build_add_channel(sequence: u32, channel_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::ADD_CHANNEL, channel_id)
}

/// Leave a chat channel.
#[must_use]
pub fn build_remove_channel(sequence: u32, channel_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::REMOVE_CHANNEL, channel_id)
}

/// A game action of one string then one `u32`.
pub(crate) fn string_u32_action(
    sequence: u32,
    action_type: GameActionType,
    text: &str,
    value: u32,
) -> Result<Vec<u8>> {
    let mut w = action(sequence, action_type);
    w.string16l(text)?;
    w.u32(value);
    Ok(w.into_vec())
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/ClientCommandRequestsTests.cs`,
    //! `InteractRequestsTests.cs` (lifestone), `InventoryActionsTests.cs`
    //! (point of interest) and the body shapes of `WorldSessionChatTests.cs`.

    use super::*;
    use crate::test_util::{BareBuilder, words};

    #[test]
    fn parameterless_actions_are_the_bare_header() {
        let cases: [(BareBuilder, u32); 11] = [
            (build_tele_to_lifestone, 0x0063),
            (build_tele_to_marketplace, 0x028D),
            (build_tele_to_pk_arena, 0x0027),
            (build_tele_to_pk_lite_arena, 0x0026),
            (build_enter_pk_lite, 0x028F),
            (build_tele_to_house, 0x0262),
            (build_tele_to_mansion, 0x0278),
            (build_suicide, 0x0279),
            (build_clear_consent, 0x0216),
            (build_display_consent, 0x0217),
            (build_index_channels, 0x0149),
        ];
        for (build, action) in cases {
            assert_eq!(build(0x1234), words(&[0xF7B1, 0x1234, action]));
        }
        assert_eq!(
            build_recall_allegiance_hometown(2),
            words(&[0xF7B1, 2, 0x02AB])
        );
    }

    #[test]
    fn one_word_actions_carry_their_argument() {
        assert_eq!(
            build_query_age(7, 0x5000_0001),
            words(&[0xF7B1, 7, 0x01C2, 0x5000_0001])
        );
        assert_eq!(
            build_query_birth(7, 0x5000_0001),
            words(&[0xF7B1, 7, 0x01C4, 0x5000_0001])
        );
        assert_eq!(build_set_afk_mode(12, true), words(&[0xF7B1, 12, 0x0F, 1]));
        assert_eq!(build_set_afk_mode(12, false), words(&[0xF7B1, 12, 0x0F, 0]));
        assert_eq!(build_tele_to_poi(3, 9), words(&[0xF7B1, 3, 0xB1, 9]));
        assert_eq!(build_list_channel(1, 42), words(&[0xF7B1, 1, 0x148, 42]));
        assert_eq!(build_add_channel(1, 42), words(&[0xF7B1, 1, 0x145, 42]));
        assert_eq!(build_remove_channel(1, 42), words(&[0xF7B1, 1, 0x146, 42]));
    }

    #[test]
    fn string_actions_pack_an_aligned_windows_1252_string() {
        for (body, action) in [
            (build_set_afk_message(11, "Bjørn").unwrap(), 0x0010),
            (build_remove_consent(11, "Bjørn").unwrap(), 0x0218),
        ] {
            assert_eq!(body.len(), 20);
            assert_eq!(body[..12], words(&[0xF7B1, 11, action]));
            assert_eq!(body[12..], [5, 0, 0x42, 0x6A, 0xF8, 0x72, 0x6E, 0]);
        }
    }
}
