//! The server removing the account: booted ([`Opcode::AccountBooted`]) or
//! banned ([`Opcode::AccountBanned`]). Either can come in any phase; the
//! server drops the connection right after.

use ac_bits::{Reader, cp1252};

use crate::{Opcode, Result, expect_opcode};

/// The account was booted (a wrong password, a wrong client version, an
/// administrator, the world closing, ...).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccountBooted {
    /// The server's reason, as the game appends it to its sentence (it
    /// usually starts with a space). `None` when the message carries none,
    /// or an empty one.
    pub reason: Option<String>,
}

impl AccountBooted {
    /// Parse an account-booted body. Only the opcode is required: a reason
    /// that is missing or cut short counts as none.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::AccountBooted)?;
        Ok(Self {
            reason: read_reason(&mut r),
        })
    }
}

/// The account is banned.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccountBanned {
    /// Seconds until the ban ends, as sent. The game reads it signed and
    /// names an end only when it is above zero.
    pub seconds_remaining: i32,
    /// The server's reason, appended to the game's sentence. `None` when
    /// the message carries none, or an empty one.
    pub reason: Option<String>,
}

impl AccountBanned {
    /// Parse an account-banned body: the opcode and the seconds are
    /// required; a reason that is missing or cut short counts as none.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::AccountBanned)?;
        let seconds_remaining = r.i32()?;
        Ok(Self {
            seconds_remaining,
            reason: read_reason(&mut r),
        })
    }
}

/// The reason string the way the game reads it: a `u16` length (`0xFFFF`
/// escapes to a `u32` length after it), then the text, which must fit in
/// what is left; the alignment padding after it is not required. A string
/// that does not fit is no string. One trailing NUL is not part of the
/// text, and nothing after a NUL is shown. An empty text is no reason.
fn read_reason(r: &mut Reader<'_>) -> Option<String> {
    let short = r.u16().ok()?;
    let len = if short == 0xFFFF {
        usize::try_from(r.u32().ok()?).ok()?
    } else {
        usize::from(short)
    };
    let bytes = r.bytes(len).ok()?;
    let stored = bytes.strip_suffix(&[0]).unwrap_or(bytes);
    if stored.is_empty() {
        return None;
    }
    let shown = stored.split(|&b| b == 0).next().unwrap_or_default();
    Some(cp1252::decode(shown))
}

#[cfg(test)]
mod tests {
    use ac_bits::Writer;

    use super::*;
    use crate::ProtoError;
    use crate::test_util::words;

    // The server's wrong-password boot: opcode 0xF7DC, then the reason as
    // a length-prefixed string padded to four bytes.
    const WRONG_PASSWORD: &str = " because the password entered for this account was not correct";

    #[test]
    fn booted_golden_bytes() {
        let mut body = vec![0xDC, 0xF7, 0x00, 0x00, 0x3E, 0x00];
        body.extend_from_slice(WRONG_PASSWORD.as_bytes());
        // 2 + 62 = 64 bytes: no padding.
        assert_eq!(body.len(), 4 + 64);
        let mut w = Writer::new();
        w.u32(0xF7DC);
        w.string16l(WRONG_PASSWORD).unwrap();
        assert_eq!(w.into_vec(), body);
        assert_eq!(
            AccountBooted::parse(&body).unwrap(),
            AccountBooted {
                reason: Some(WRONG_PASSWORD.to_owned())
            }
        );
    }

    #[test]
    fn booted_without_a_reason_has_none() {
        // The bare opcode, an empty string, a lone NUL, and a string longer
        // than the body: all no reason.
        for body in [
            words(&[0xF7DC]),
            vec![0xDC, 0xF7, 0, 0, 0, 0, 0, 0],
            vec![0xDC, 0xF7, 0, 0, 1, 0, 0, 0],
            vec![0xDC, 0xF7, 0, 0, 9, 0, b'a', b'b'],
            vec![0xDC, 0xF7, 0, 0, 0xFF],
        ] {
            assert_eq!(
                AccountBooted::parse(&body).unwrap(),
                AccountBooted { reason: None },
                "{body:02X?}"
            );
        }
    }

    #[test]
    fn a_reason_needs_no_padding_and_stops_at_a_nul() {
        // " x" with no padding after it.
        let body = [0xDC, 0xF7, 0, 0, 2, 0, b' ', b'x'];
        assert_eq!(
            AccountBooted::parse(&body).unwrap().reason.as_deref(),
            Some(" x")
        );
        // A trailing NUL is dropped; text after an inner NUL is not shown.
        let body = [0xDC, 0xF7, 0, 0, 4, 0, b'a', 0, b'b', 0];
        assert_eq!(
            AccountBooted::parse(&body).unwrap().reason.as_deref(),
            Some("a")
        );
        // The long form: 0xFFFF, then a u32 length.
        let body = [0xDC, 0xF7, 0, 0, 0xFF, 0xFF, 1, 0, 0, 0, b'z'];
        assert_eq!(
            AccountBooted::parse(&body).unwrap().reason.as_deref(),
            Some("z")
        );
        // Windows-1252 text.
        let body = [0xDC, 0xF7, 0, 0, 1, 0, 0xE9, 0];
        assert_eq!(
            AccountBooted::parse(&body).unwrap().reason.as_deref(),
            Some("\u{e9}")
        );
    }

    // The server's ban: opcode 0xF7C1, the seconds left as a u32, then
    // " - <reason>" as a padded string.
    #[test]
    fn banned_golden_bytes() {
        let body = [
            0xC1, 0xF7, 0x00, 0x00, // opcode 0xF7C1
            0x10, 0x0E, 0x00, 0x00, // 3600 seconds
            0x0B, 0x00, b' ', b'-', b' ', b'c', b'h', b'e', b'a', b't', b'i', b'n', b'g', 0x00,
            0x00, 0x00, // " - cheating", padded: 2 + 11 + 3 = 16
        ];
        let mut w = Writer::new();
        w.u32(0xF7C1);
        w.u32(3600);
        w.string16l(" - cheating").unwrap();
        assert_eq!(w.into_vec(), body);
        assert_eq!(
            AccountBanned::parse(&body).unwrap(),
            AccountBanned {
                seconds_remaining: 3600,
                reason: Some(" - cheating".to_owned()),
            }
        );
    }

    #[test]
    fn banned_reads_the_seconds_signed_and_needs_them() {
        let parsed = AccountBanned::parse(&words(&[0xF7C1, 0xFFFF_FFFF])).unwrap();
        assert_eq!(parsed.seconds_remaining, -1);
        assert_eq!(parsed.reason, None);
        for len in 0..8 {
            assert!(AccountBanned::parse(&words(&[0xF7C1, 5])[..len]).is_err());
        }
    }

    #[test]
    fn wrong_opcodes_are_errors() {
        assert!(matches!(
            AccountBooted::parse(&words(&[0xF7C1, 0])),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert!(matches!(
            AccountBanned::parse(&words(&[0xF7DC, 0])),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert!(AccountBooted::parse(&[0xDC, 0xF7]).is_err());
    }
}
