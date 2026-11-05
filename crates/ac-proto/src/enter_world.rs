use ac_bits::Writer;

use crate::{Opcode, Result};

/// Body asking the server to enter the world ([`Opcode::CharacterEnterWorldRequest`]):
/// the bare opcode. The server answers with [`Opcode::ServerReady`].
///
/// Sent on the UI queue.
#[must_use]
pub fn build_enter_world_request() -> Vec<u8> {
    Opcode::CharacterEnterWorldRequest.bare_body()
}

/// Body entering the world with a character ([`Opcode::CharacterEnterWorld`]):
/// opcode, character id, account name. Send it only after
/// [`Opcode::ServerReady`] arrives. `account_name` must be the spelling from
/// the character list, not what the user typed.
///
/// Sent on the UI queue.
pub fn build_enter_world(character_id: u32, account_name: &str) -> Result<Vec<u8>> {
    let mut w = Writer::with_capacity(32);
    w.u32(Opcode::CharacterEnterWorld.value());
    w.u32(character_id);
    w.string16l(account_name)?;
    Ok(w.into_vec())
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/CharacterEnterWorldTests.cs`.

    use super::*;

    #[test]
    fn request_is_just_the_opcode() {
        assert_eq!(build_enter_world_request(), [0xC8, 0xF7, 0x00, 0x00]);
    }

    #[test]
    fn enter_world_layout_is_opcode_guid_padded_account() {
        let body = build_enter_world(0x5000_0001, "testaccount").unwrap();
        let expected: &[u8] = &[
            0x57, 0xF6, 0x00, 0x00, // opcode 0xF657
            0x01, 0x00, 0x00, 0x50, // guid 0x50000001
            0x0B, 0x00, // length 11
            b't', b'e', b's', b't', b'a', b'c', b'c', b'o', b'u', b'n', b't', //
            0x00, 0x00, 0x00, // 2 + 11 = 13, padded to 16
        ];
        assert_eq!(body, expected);
        assert_eq!(body.len(), 4 + 4 + 16);
    }

    #[test]
    fn overlong_account_name_is_an_error() {
        let name = "a".repeat(usize::from(u16::MAX) + 1);
        assert!(build_enter_world(1, &name).is_err());
    }
}
