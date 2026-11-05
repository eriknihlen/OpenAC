use ac_bits::Writer;

use crate::Opcode;

/// Body asking the server to log the character off
/// ([`Opcode::CharacterLogOff`]): opcode, character id. The server confirms
/// with the bare opcode (see [`is_logoff_confirmation`]).
///
/// Sent on the UI queue.
#[must_use]
pub fn build_logoff_request(character_id: u32) -> Vec<u8> {
    let mut w = Writer::with_capacity(8);
    w.u32(Opcode::CharacterLogOff.value());
    w.u32(character_id);
    w.into_vec()
}

/// True if `body` is the server's log-off confirmation: exactly the
/// four-byte [`Opcode::CharacterLogOff`] and nothing else.
#[must_use]
pub fn is_logoff_confirmation(body: &[u8]) -> bool {
    body == Opcode::CharacterLogOff.value().to_le_bytes()
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/CharacterLogOffTests.cs`.

    use super::*;

    #[test]
    fn request_is_opcode_then_character_id() {
        assert_eq!(
            build_logoff_request(0x5000_000A),
            [0x53, 0xF6, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x50]
        );
    }

    #[test]
    fn confirmation_is_the_bare_opcode() {
        assert!(is_logoff_confirmation(&[0x53, 0xF6, 0x00, 0x00]));
    }

    #[test]
    fn truncated_or_different_message_is_not_a_confirmation() {
        assert!(!is_logoff_confirmation(&[0x53, 0xF6, 0x00]));
        assert!(!is_logoff_confirmation(&0xF654u32.to_le_bytes()));
        assert!(!is_logoff_confirmation(&[]));
    }

    #[test]
    fn request_shaped_body_is_not_a_confirmation() {
        assert!(!is_logoff_confirmation(&build_logoff_request(0x5000_000A)));
    }
}
