use ac_bits::Reader;

use crate::{Opcode, ProtoError, Result, build_enter_world, expect_opcode};

/// Bytes the smallest character entry takes: id, empty name, greyed-out
/// seconds. Bounds a count before anything is allocated for it.
const MIN_CHARACTER_SIZE: usize = 12;

/// One character slot in the list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Character {
    /// Character object id; zero marks an empty slot.
    pub id: u32,
    /// Character name.
    pub name: String,
    /// Seconds until a pending deletion completes; non-zero means the
    /// character is greyed out and cannot enter the world.
    pub seconds_greyed_out: u32,
}

impl Character {
    /// True for an occupied slot that is not pending deletion: the only
    /// kind of character that may enter the world.
    #[must_use]
    pub fn is_available_active_identity(&self) -> bool {
        self.id != 0 && self.seconds_greyed_out == 0
    }
}

/// The account's characters, sent by the server after login
/// ([`Opcode::CharacterList`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CharacterList {
    /// Status word that precedes the lists.
    pub status: u32,
    /// Active characters, in slot order.
    pub characters: Vec<Character>,
    /// Deleted characters.
    pub deleted_characters: Vec<Character>,
    /// Character slots the account has; `-1` is a valid sentinel.
    pub slot_count: i32,
    /// The account name as the server spells it. The enter-world message
    /// must carry this spelling, not what the user typed.
    pub account_name: String,
    /// Whether the server uses the Turbine chat system.
    pub use_turbine_chat: bool,
    /// Whether the account has the Throne of Destiny expansion.
    pub has_throne_of_destiny: bool,
}

/// A character chosen for entering the world, with the enter-world body
/// ready to send once the server is ready.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EnterWorldSelection {
    /// The chosen character.
    pub character: Character,
    /// Body built by [`build_enter_world`] from the character and the list's
    /// account name.
    pub enter_world_body: Vec<u8>,
}

/// Why a character could not be selected for entering the world.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum SelectionError {
    /// The index is outside the active character list.
    #[error("character index {index} is outside the {count} active characters")]
    IndexOutOfRange {
        /// Requested index.
        index: usize,
        /// Number of active characters.
        count: usize,
    },
    /// The slot is empty or pending deletion.
    #[error("character {index} is not an active, non-greyed identity")]
    Unavailable {
        /// Requested index.
        index: usize,
    },
    /// The enter-world body could not be encoded.
    #[error(transparent)]
    Encode(#[from] ProtoError),
}

impl CharacterList {
    /// Parse a character-list body.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::CharacterList)?;
        let status = r.u32()?;
        let characters = read_characters(&mut r, "active character")?;
        let deleted_characters = read_characters(&mut r, "deleted character")?;
        let slot_count = r.i32()?;
        let account_name = r.string16l()?;
        let use_turbine_chat = r.u32()? != 0;
        let has_throne_of_destiny = r.u32()? != 0;
        Ok(Self {
            status,
            characters,
            deleted_characters,
            slot_count,
            account_name,
            use_turbine_chat,
            has_throne_of_destiny,
        })
    }

    /// The first active character that may enter the world, with its index
    /// in [`CharacterList::characters`]. Deleted characters are never
    /// considered.
    #[must_use]
    pub fn first_available(&self) -> Option<(usize, &Character)> {
        self.characters
            .iter()
            .enumerate()
            .find(|(_, c)| c.is_available_active_identity())
    }

    /// Choose the active character at `index` for entering the world and
    /// build its enter-world body with this list's account name.
    pub fn select_for_enter_world(
        &self,
        index: usize,
    ) -> core::result::Result<EnterWorldSelection, SelectionError> {
        let chosen = self
            .characters
            .get(index)
            .ok_or(SelectionError::IndexOutOfRange {
                index,
                count: self.characters.len(),
            })?;
        if !chosen.is_available_active_identity() {
            return Err(SelectionError::Unavailable { index });
        }
        Ok(EnterWorldSelection {
            character: chosen.clone(),
            enter_world_body: build_enter_world(chosen.id, &self.account_name)?,
        })
    }
}

fn read_characters(r: &mut Reader<'_>, what: &'static str) -> Result<Vec<Character>> {
    let count = r.u32()?;
    let fits = usize::try_from(count).is_ok_and(|n| n <= r.remaining() / MIN_CHARACTER_SIZE);
    if !fits {
        return Err(ProtoError::CountExceedsPayload { what, count });
    }
    (0..count)
        .map(|_| {
            Ok(Character {
                id: r.u32()?,
                name: r.string16l()?,
                seconds_greyed_out: r.u32()?,
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/CharacterListTests.cs`
    //! and `CharacterEnterWorldTests.cs` (session selection).

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::assert_every_prefix_fails;

    fn header(w: &mut Writer, status: u32) {
        w.u32(Opcode::CharacterList.value());
        w.u32(status);
    }

    fn identity(w: &mut Writer, id: u32, name: &str, seconds_greyed_out: u32) {
        w.u32(id);
        w.string16l(name).unwrap();
        w.u32(seconds_greyed_out);
    }

    fn tail(w: &mut Writer, slot_count: i32, account: &str, turbine_chat: bool, tod: bool) {
        w.i32(slot_count);
        w.string16l(account).unwrap();
        w.u32(u32::from(turbine_chat));
        w.u32(u32::from(tod));
    }

    fn character(id: u32, name: &str, seconds_greyed_out: u32) -> Character {
        Character {
            id,
            name: name.to_owned(),
            seconds_greyed_out,
        }
    }

    fn list(active: Vec<Character>, deleted: Vec<Character>, account: &str) -> CharacterList {
        CharacterList {
            status: 0,
            characters: active,
            deleted_characters: deleted,
            slot_count: 11,
            account_name: account.to_owned(),
            use_turbine_chat: true,
            has_throne_of_destiny: true,
        }
    }

    #[test]
    fn single_character_extracts_guid_and_name() {
        let mut w = Writer::new();
        header(&mut w, 0);
        w.u32(1);
        identity(&mut w, 0x5000_0001, "+Acdream", 0);
        w.u32(0);
        tail(&mut w, 11, "testaccount", true, true);

        let parsed = CharacterList::parse(w.as_slice()).unwrap();

        assert_eq!(parsed.status, 0);
        assert_eq!(parsed.characters, [character(0x5000_0001, "+Acdream", 0)]);
        assert!(parsed.deleted_characters.is_empty());
        assert_eq!(parsed.slot_count, 11);
        assert_eq!(parsed.account_name, "testaccount");
        assert!(parsed.use_turbine_chat);
        assert!(parsed.has_throne_of_destiny);
    }

    #[test]
    fn golden_bytes_of_a_one_character_list() {
        // The single-character case above, spelled out byte by byte.
        let body: &[u8] = &[
            0x58, 0xF6, 0x00, 0x00, // opcode 0xF658
            0x00, 0x00, 0x00, 0x00, // status
            0x01, 0x00, 0x00, 0x00, // one active character
            0x01, 0x00, 0x00, 0x50, // id 0x50000001
            0x08, 0x00, b'+', b'A', b'c', b'd', b'r', b'e', b'a', b'm', 0x00,
            0x00, // name + pad
            0x00, 0x00, 0x00, 0x00, // seconds greyed out
            0x00, 0x00, 0x00, 0x00, // no deleted characters
            0x0B, 0x00, 0x00, 0x00, // 11 slots
            0x0B, 0x00, b't', b'e', b's', b't', b'a', b'c', b'c', b'o', b'u', b'n', b't', 0x00,
            0x00, 0x00, // account + pad
            0x01, 0x00, 0x00, 0x00, // turbine chat
            0x01, 0x00, 0x00, 0x00, // throne of destiny
        ];
        let parsed = CharacterList::parse(body).unwrap();
        assert_eq!(parsed.characters, [character(0x5000_0001, "+Acdream", 0)]);
        assert_eq!(parsed.account_name, "testaccount");
    }

    #[test]
    fn multiple_characters_preserve_order() {
        let mut w = Writer::new();
        header(&mut w, 0);
        w.u32(3);
        identity(&mut w, 0x5000_0001, "Alice", 0);
        identity(&mut w, 0x5000_0002, "Bob", 0);
        identity(&mut w, 0x5000_0003, "Carol", 0);
        w.u32(0);
        tail(&mut w, 11, "acct", false, true);

        let parsed = CharacterList::parse(w.as_slice()).unwrap();

        let names: Vec<&str> = parsed.characters.iter().map(|c| c.name.as_str()).collect();
        assert_eq!(names, ["Alice", "Bob", "Carol"]);
    }

    #[test]
    fn nonzero_status_and_deleted_collection_preserve_layout() {
        let mut w = Writer::new();
        header(&mut w, 7);
        w.u32(1);
        identity(&mut w, 0x5000_0001, "Active", 0);
        w.u32(2);
        identity(&mut w, 0x5000_0002, "Deleted One", 90);
        identity(&mut w, 0x5000_0003, "Deleted Two", 180);
        tail(&mut w, 13, "CanonicalAccount", true, false);

        let parsed = CharacterList::parse(w.as_slice()).unwrap();

        assert_eq!(parsed.status, 7);
        assert_eq!(parsed.characters, [character(0x5000_0001, "Active", 0)]);
        assert_eq!(
            parsed.deleted_characters,
            [
                character(0x5000_0002, "Deleted One", 90),
                character(0x5000_0003, "Deleted Two", 180),
            ]
        );
        assert_eq!(parsed.slot_count, 13);
        assert_eq!(parsed.account_name, "CanonicalAccount");
        assert!(parsed.use_turbine_chat);
        assert!(!parsed.has_throne_of_destiny);
    }

    #[test]
    fn first_available_skips_zero_guid_and_greyed_characters() {
        let parsed = list(
            vec![
                character(0, "Empty", 0),
                character(0x5000_0001, "Deleting", 30),
                character(0x5000_0002, "Ready", 0),
            ],
            vec![character(0x5000_0003, "Deleted", 0)],
            "Account",
        );

        let (index, chosen) = parsed.first_available().unwrap();
        assert_eq!(index, 2);
        assert_eq!(chosen.id, 0x5000_0002);
    }

    #[test]
    fn first_available_rejects_all_greyed_and_never_reads_deleted() {
        let parsed = list(
            vec![character(0x5000_0001, "Deleting", 1)],
            vec![character(0x5000_0002, "Deleted Ready", 0)],
            "Account",
        );
        assert_eq!(parsed.first_available(), None);
    }

    #[test]
    fn empty_collections_and_negative_slot_sentinel_are_preserved() {
        let mut w = Writer::new();
        header(&mut w, 0);
        w.u32(0);
        w.u32(0);
        w.u32(u32::MAX);
        w.string16l("Account").unwrap();
        w.u32(2);
        w.u32(u32::MAX);

        let parsed = CharacterList::parse(w.as_slice()).unwrap();

        assert!(parsed.characters.is_empty());
        assert!(parsed.deleted_characters.is_empty());
        assert_eq!(parsed.slot_count, -1);
        assert!(parsed.use_turbine_chat);
        assert!(parsed.has_throne_of_destiny);
        assert_eq!(parsed.first_available(), None);
    }

    #[test]
    fn count_that_cannot_fit_the_remaining_payload_is_rejected() {
        let active = crate::test_util::words(&[0xF658, 0, 256]);
        assert_eq!(
            CharacterList::parse(&active),
            Err(ProtoError::CountExceedsPayload {
                what: "active character",
                count: 256
            })
        );
        let deleted = crate::test_util::words(&[0xF658, 0, 0, 256]);
        assert_eq!(
            CharacterList::parse(&deleted),
            Err(ProtoError::CountExceedsPayload {
                what: "deleted character",
                count: 256
            })
        );
    }

    #[test]
    fn huge_count_is_rejected_before_allocating() {
        let body = crate::test_util::words(&[0xF658, 0, u32::MAX]);
        assert!(matches!(
            CharacterList::parse(&body),
            Err(ProtoError::CountExceedsPayload { .. })
        ));
    }

    #[test]
    fn minimum_size_identities_use_the_payload_bound_not_a_cap() {
        let mut w = Writer::new();
        header(&mut w, 0);
        w.u32(255);
        for id in 1..=255 {
            identity(&mut w, id, "", 0);
        }
        w.u32(0);
        tail(&mut w, 255, "Account", true, true);

        let parsed = CharacterList::parse(w.as_slice()).unwrap();

        assert_eq!(parsed.characters.len(), 255);
        assert_eq!(parsed.slot_count, 255);
    }

    #[test]
    fn every_truncated_prefix_is_an_error() {
        let mut w = Writer::new();
        header(&mut w, 3);
        w.u32(1);
        identity(&mut w, 0x5000_0001, "A", 0);
        w.u32(1);
        identity(&mut w, 0x5000_0002, "Deleted", 22);
        tail(&mut w, 11, "Account", true, true);

        assert_every_prefix_fails(w.as_slice(), CharacterList::parse);
    }

    #[test]
    fn wrong_opcode_is_an_error() {
        assert_eq!(
            CharacterList::parse(&0xDEAD_BEEFu32.to_le_bytes()),
            Err(ProtoError::WrongOpcode {
                expected: 0xF658,
                actual: 0xDEAD_BEEF
            })
        );
    }

    #[test]
    fn selection_uses_the_list_account_spelling_in_the_enter_world_body() {
        let parsed = list(
            vec![character(0x5000_0001, "Ready", 0)],
            vec![],
            "CanonicalCase",
        );

        let selection = parsed.select_for_enter_world(0).unwrap();

        assert_eq!(selection.character.id, 0x5000_0001);
        assert_eq!(
            selection.enter_world_body,
            build_enter_world(0x5000_0001, "CanonicalCase").unwrap()
        );
        let body = &selection.enter_world_body;
        assert_eq!(body[..4], 0xF657u32.to_le_bytes());
        assert_eq!(body[4..8], 0x5000_0001u32.to_le_bytes());
        let len = usize::from(u16::from_le_bytes([body[8], body[9]]));
        assert_eq!(&body[10..10 + len], b"CanonicalCase");
    }

    #[test]
    fn selection_rejects_unavailable_identities() {
        for (guid, greyed) in [(0, 0), (0x5000_0001, 1), (0x5000_0001, 30)] {
            let parsed = list(vec![character(guid, "Unavailable", greyed)], vec![], "A");
            assert_eq!(
                parsed.select_for_enter_world(0),
                Err(SelectionError::Unavailable { index: 0 })
            );
        }
    }

    #[test]
    fn selection_rejects_an_index_outside_the_active_list() {
        let parsed = list(vec![character(0x5000_0001, "Ready", 0)], vec![], "A");
        assert_eq!(
            parsed.select_for_enter_world(1),
            Err(SelectionError::IndexOutOfRange { index: 1, count: 1 })
        );
    }
}
