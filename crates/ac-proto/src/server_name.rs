use ac_bits::{Reader, cp1252};

use crate::{Opcode, Result, expect_opcode};

/// The world's name and population, sent by the server during login
/// ([`Opcode::ServerName`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerName {
    /// Players connected now.
    pub current_connections: i32,
    /// Connection limit; the sign is kept as sent (`-1` occurs).
    pub max_connections: i32,
    /// World name.
    pub world_name: String,
}

impl ServerName {
    /// Parse a server-name body.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::ServerName)?;
        let current_connections = r.i32()?;
        let max_connections = r.i32()?;
        // The name is the last field, so its alignment padding is not
        // required to be present.
        let len = usize::from(r.u16()?);
        let world_name = cp1252::decode(r.bytes(len)?);
        Ok(Self {
            current_connections,
            max_connections,
            world_name,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/ServerNameTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::ProtoError;
    use crate::test_util::assert_every_prefix_fails;

    fn body(current: i32, max: i32, name: &str) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(Opcode::ServerName.value());
        w.i32(current);
        w.i32(max);
        w.string16l(name).unwrap();
        w.into_vec()
    }

    #[test]
    fn exact_fields() {
        let parsed = ServerName::parse(&body(123, 1000, "sawato")).unwrap();
        assert_eq!(
            parsed,
            ServerName {
                current_connections: 123,
                max_connections: 1000,
                world_name: "sawato".to_owned(),
            }
        );
    }

    #[test]
    fn golden_bytes() {
        let bytes: &[u8] = &[
            0xE1, 0xF7, 0x00, 0x00, // opcode 0xF7E1
            0x7B, 0x00, 0x00, 0x00, // 123
            0xE8, 0x03, 0x00, 0x00, // 1000
            0x06, 0x00, b's', b'a', b'w', b'a', b't', b'o', // no padding: 2 + 6 = 8
        ];
        assert_eq!(body(123, 1000, "sawato"), bytes);
        assert_eq!(ServerName::parse(bytes).unwrap().world_name, "sawato");
    }

    #[test]
    fn negative_max_connections_keeps_its_sign() {
        let parsed = ServerName::parse(&body(0, -1, "Frostfell")).unwrap();
        assert_eq!(parsed.current_connections, 0);
        assert_eq!(parsed.max_connections, -1);
        assert_eq!(parsed.world_name, "Frostfell");
    }

    #[test]
    fn empty_world_name_round_trips() {
        assert_eq!(ServerName::parse(&body(0, 0, "")).unwrap().world_name, "");
    }

    #[test]
    fn missing_trailing_padding_is_accepted() {
        let mut bytes = body(0, 0, "Frostfell");
        bytes.pop(); // "Frostfell" is 2 + 9 bytes, so one padding byte
        assert_eq!(ServerName::parse(&bytes).unwrap().world_name, "Frostfell");
    }

    #[test]
    fn wrong_opcode_is_an_error() {
        assert!(matches!(
            ServerName::parse(&0xDEAD_BEEFu32.to_le_bytes()),
            Err(ProtoError::WrongOpcode { .. })
        ));
    }

    #[test]
    fn truncated_bodies_are_errors() {
        // Every prefix that cuts into the counts, the length or the name.
        let full = body(5, 10, "sawato");
        assert_every_prefix_fails(&full, ServerName::parse);
    }
}
