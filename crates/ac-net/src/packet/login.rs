//! The login request: the body of the first packet a client sends.

use ac_bits::{Reader, Writer};

/// How the login authenticates.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NetAuthType {
    /// Not set.
    Undefined,
    /// Account name only.
    Account,
    /// Account name and password.
    AccountPassword,
    /// A launcher ticket.
    GlsTicket,
    /// Any other value.
    Other(u32),
}

impl NetAuthType {
    /// The wire value.
    #[must_use]
    pub const fn to_u32(self) -> u32 {
        match self {
            Self::Undefined => 0,
            Self::Account => 1,
            Self::AccountPassword => 2,
            Self::GlsTicket => 0x4000_0002,
            Self::Other(value) => value,
        }
    }

    /// From the wire value.
    #[must_use]
    pub const fn from_u32(value: u32) -> Self {
        match value {
            0 => Self::Undefined,
            1 => Self::Account,
            2 => Self::AccountPassword,
            0x4000_0002 => Self::GlsTicket,
            other => Self::Other(other),
        }
    }
}

/// The client version string the retail client sends.
pub const RETAIL_CLIENT_VERSION: &str = "1802";

/// Why a login request could not be built.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum LoginBuildError {
    /// The account name does not fit a 16-bit length.
    #[error("account name of {0} characters is too long")]
    AccountTooLong(usize),
    /// The password is longer than the 255 characters the short form holds.
    #[error("password of {0} characters is too long (at most 255)")]
    PasswordTooLong(usize),
}

/// Why a login request body could not be read.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum LoginParseError {
    /// The body ended inside the named field.
    #[error("login request truncated in {0}")]
    Truncated(&'static str),
}

/// The fields of a login request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LoginRequestFields {
    /// Client version string.
    pub client_version: String,
    /// Bytes after the length field.
    pub body_length: u32,
    /// Authentication kind.
    pub auth_type: NetAuthType,
    /// Authentication flags.
    pub auth_flags: u32,
    /// Seconds since the Unix epoch when the request was built.
    pub timestamp: u32,
    /// Account name.
    pub account: String,
    /// Admin "log in as" name (empty for normal accounts).
    pub login_as: String,
    /// Password, for account/password logins.
    pub password: Option<String>,
    /// Ticket, for launcher logins.
    pub gls_ticket: Option<String>,
}

/// The credentials are written as ASCII; any other character becomes `?`.
fn ascii(text: &str) -> Vec<u8> {
    text.chars()
        .map(|c| if c.is_ascii() { c as u8 } else { b'?' })
        .collect()
}

/// The long-string form used for the password: a u32 length that counts a
/// one-byte marker, the marker, the text, then zero padding so the record is
/// a multiple of four bytes. Empty is just a zero length.
fn write_string32l(w: &mut Writer, text: &[u8]) {
    if text.is_empty() {
        w.u32(0);
        return;
    }
    // Callers cap the text at 255 bytes.
    w.u32(text.len() as u32 + 1);
    w.u8(0);
    w.bytes(text);
    w.pad((4 - ((4 + 1 + text.len()) & 3)) & 3);
}

fn read_string32l(r: &mut Reader<'_>) -> Result<String, LoginParseError> {
    let outer = r
        .u32()
        .map_err(|_| LoginParseError::Truncated("string32 length"))?;
    if outer == 0 {
        return Ok(String::new());
    }
    r.skip(1)
        .map_err(|_| LoginParseError::Truncated("string32 marker"))?;
    let mut len = outer - 1;
    if len > 255 {
        // Long strings carry a second marker byte.
        r.skip(1)
            .map_err(|_| LoginParseError::Truncated("string32 marker"))?;
        len -= 1;
    }
    let text = r
        .bytes(len as usize)
        .map_err(|_| LoginParseError::Truncated("string32 text"))?;
    let pad = (4 - ((4 + outer as usize) & 3)) & 3;
    // Trailing padding may be cut off at the very end of the body.
    let _ = r.skip(pad.min(r.remaining()));
    Ok(text.iter().map(|&b| char::from(b)).collect())
}

fn read_string16l(r: &mut Reader<'_>, field: &'static str) -> Result<String, LoginParseError> {
    let len = usize::from(r.u16().map_err(|_| LoginParseError::Truncated(field))?);
    let text = r
        .bytes(len)
        .map_err(|_| LoginParseError::Truncated(field))?;
    let pad = (4 - ((2 + len) & 3)) & 3;
    let _ = r.skip(pad.min(r.remaining()));
    Ok(text.iter().map(|&b| char::from(b)).collect())
}

/// Build an account/password login request body.
pub fn build_login_request(
    account: &str,
    password: &str,
    timestamp: u32,
) -> Result<Vec<u8>, LoginBuildError> {
    let account = ascii(account);
    let password = ascii(password);
    if account.len() > usize::from(u16::MAX) {
        return Err(LoginBuildError::AccountTooLong(account.len()));
    }
    if password.len() > 255 {
        return Err(LoginBuildError::PasswordTooLong(password.len()));
    }

    let mut w = Writer::with_capacity(128);
    // Short strings cannot overflow their 16-bit length (checked above).
    let _ = w.string16l_bytes(RETAIL_CLIENT_VERSION.as_bytes());
    let length_at = w.len();
    w.u32(0); // patched below
    let body_start = w.len();
    w.u32(NetAuthType::AccountPassword.to_u32());
    w.u32(0); // auth flags
    w.u32(timestamp);
    let _ = w.string16l_bytes(&account);
    let _ = w.string16l_bytes(b""); // "log in as": empty for normal accounts
    write_string32l(&mut w, &password);

    let body_length = (w.len() - body_start) as u32;
    w.as_mut_slice()[length_at..length_at + 4].copy_from_slice(&body_length.to_le_bytes());
    Ok(w.into_vec())
}

/// Read a login request body.
pub fn parse_login_request(bytes: &[u8]) -> Result<LoginRequestFields, LoginParseError> {
    let mut r = Reader::new(bytes);
    let client_version = read_string16l(&mut r, "client version")?;
    let body_length = r
        .u32()
        .map_err(|_| LoginParseError::Truncated("body length"))?;
    let auth_type = NetAuthType::from_u32(
        r.u32()
            .map_err(|_| LoginParseError::Truncated("auth type"))?,
    );
    let auth_flags = r
        .u32()
        .map_err(|_| LoginParseError::Truncated("auth flags"))?;
    let timestamp = r
        .u32()
        .map_err(|_| LoginParseError::Truncated("timestamp"))?;
    let account = read_string16l(&mut r, "account")?;
    let login_as = read_string16l(&mut r, "login as")?;
    let (password, gls_ticket) = match auth_type {
        NetAuthType::AccountPassword => (Some(read_string32l(&mut r)?), None),
        NetAuthType::GlsTicket => (None, Some(read_string32l(&mut r)?)),
        _ => (None, None),
    };
    Ok(LoginRequestFields {
        client_version,
        body_length,
        auth_type,
        auth_flags,
        timestamp,
        account,
        login_as,
        password,
        gls_ticket,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::packet::{PacketFlags, PacketHeader, decode_with_keystream, encode};

    // Source: OpenAC tests `Packets/LoginRequestTests.cs`.
    #[test]
    fn build_then_parse_round_trips_every_field() {
        let bytes = build_login_request("testaccount", "testpassword", 0x1234_5678).unwrap();
        let parsed = parse_login_request(&bytes).unwrap();
        assert_eq!(parsed.client_version, RETAIL_CLIENT_VERSION);
        assert_eq!(parsed.auth_type, NetAuthType::AccountPassword);
        assert_eq!(parsed.auth_flags, 0);
        assert_eq!(parsed.timestamp, 0x1234_5678);
        assert_eq!(parsed.account, "testaccount");
        assert_eq!(parsed.login_as, "");
        assert_eq!(parsed.password.as_deref(), Some("testpassword"));
        assert_eq!(parsed.gls_ticket, None);
        // Every field pads to four bytes.
        assert_eq!(bytes.len() % 4, 0);

        let empty = parse_login_request(&build_login_request("", "", 1).unwrap()).unwrap();
        assert_eq!(empty.account, "");
        assert_eq!(empty.password.as_deref(), Some(""));
    }

    // Source: OpenAC tests `LoginRequestTests.Build_BodyLength_ReflectsActualRemainingBytes`.
    #[test]
    fn body_length_counts_the_bytes_after_it() {
        let bytes = build_login_request("u", "p", 0).unwrap();
        let parsed = parse_login_request(&bytes).unwrap();
        // "1802" record = 8 bytes, then the 4-byte length itself.
        assert_eq!(parsed.body_length as usize, bytes.len() - 12);
    }

    /// Hand-assembled golden bytes for a short request, field by field.
    #[test]
    fn golden_layout() {
        let bytes = build_login_request("ab", "xyz", 0x0102_0304).unwrap();
        let expected: Vec<u8> = [
            &[4, 0, b'1', b'8', b'0', b'2', 0, 0][..],
            &[28, 0, 0, 0], // body length
            &[2, 0, 0, 0],  // account + password
            &[0, 0, 0, 0],  // flags
            &[4, 3, 2, 1],  // timestamp
            &[2, 0, b'a', b'b'],
            &[0, 0, 0, 0], // empty "log in as" (2 + 2 padding)
            &[4, 0, 0, 0, 0, b'x', b'y', b'z'],
        ]
        .concat();
        assert_eq!(bytes, expected);
    }

    #[test]
    fn non_ascii_becomes_question_mark_and_long_passwords_fail() {
        let parsed = parse_login_request(&build_login_request("é", "pw", 0).unwrap()).unwrap();
        assert_eq!(parsed.account, "?");
        assert_eq!(
            build_login_request("a", &"p".repeat(256), 0).unwrap_err(),
            LoginBuildError::PasswordTooLong(256)
        );
    }

    #[test]
    fn truncated_body_is_an_error() {
        let bytes = build_login_request("testaccount", "testpassword", 1).unwrap();
        for cut in [0, 3, 10, 20, 30] {
            assert!(parse_login_request(&bytes[..cut]).is_err(), "cut {cut}");
        }
    }

    // Source: OpenAC tests `LoginRequestTests.LoginRequest_EmbeddedInPacket_DecodableByPacketCodec`.
    #[test]
    fn login_packet_is_cleartext_and_carries_the_body_as_its_optional_section() {
        let payload = build_login_request("testaccount", "testpassword", 1).unwrap();
        let header = PacketHeader {
            flags: PacketFlags::LOGIN_REQUEST,
            ..PacketHeader::default()
        };
        let datagram = encode(header, &payload, None).unwrap();
        let packet = decode_with_keystream(&datagram, None).unwrap();
        assert_eq!(packet.body, &payload[..]);
        assert_eq!(packet.optional.raw, &payload[..]);
        assert_eq!(packet.fragment_count, 0);
        let parsed = parse_login_request(packet.body).unwrap();
        assert_eq!(parsed.account, "testaccount");
        assert_eq!(parsed.password.as_deref(), Some("testpassword"));
    }
}
