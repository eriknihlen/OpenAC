use core::fmt;
use core::ops::{BitAnd, BitOr, BitOrAssign};

/// The packet header's flags word: which optional sections follow the
/// header and how the checksum is built.
///
/// Two comparisons matter and are deliberately different: [`PacketFlags::has`]
/// asks "is any of these bits set", while `==` asks "are these exactly the
/// flags" — the server treats an ack-only packet by exact equality.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct PacketFlags(pub u32);

macro_rules! flags {
    ($($(#[$doc:meta])* $name:ident = $value:expr,)*) => {
        impl PacketFlags {
            $($(#[$doc])* pub const $name: Self = Self($value);)*
            const NAMES: &'static [(&'static str, u32)] = &[$((stringify!($name), $value),)*];
        }
    };
}

flags! {
    /// No flags.
    NONE = 0x0000_0000,
    /// A resend of a packet sent before.
    RETRANSMISSION = 0x0000_0001,
    /// The payload checksum is XORed with a keystream word.
    ENCRYPTED_CHECKSUM = 0x0000_0002,
    /// Message fragments follow the optional sections.
    BLOB_FRAGMENTS = 0x0000_0004,
    /// Server switch (8 bytes).
    SERVER_SWITCH = 0x0000_0100,
    /// Logon server address.
    LOGON_SERVER_ADDR = 0x0000_0200,
    /// Unused header bit.
    EMPTY_HEADER1 = 0x0000_0400,
    /// Referral to another server.
    REFERRAL = 0x0000_0800,
    /// A list of sequence numbers the sender wants resent (a NAK).
    REQUEST_RETRANSMIT = 0x0000_1000,
    /// A list of sequence numbers that will never be resent.
    REJECT_RETRANSMIT = 0x0000_2000,
    /// A cumulative acknowledgement (4 bytes).
    ACK_SEQUENCE = 0x0000_4000,
    /// The sender is closing the connection.
    DISCONNECT = 0x0000_8000,
    /// Account login (the rest of the body).
    LOGIN_REQUEST = 0x0001_0000,
    /// World login (8 bytes).
    WORLD_LOGIN_REQUEST = 0x0002_0000,
    /// Server's reply to a login: seeds and cookie (32 bytes).
    CONNECT_REQUEST = 0x0004_0000,
    /// Client's echo of the cookie (8 bytes).
    CONNECT_RESPONSE = 0x0008_0000,
    /// Network error.
    NET_ERROR = 0x0010_0000,
    /// Network error with disconnect.
    NET_ERROR_DISCONNECT = 0x0020_0000,
    /// Command (8 bytes).
    CICMD_COMMAND = 0x0040_0000,
    /// The sender's clock (8-byte double).
    TIME_SYNC = 0x0100_0000,
    /// Echo request (4-byte float).
    ECHO_REQUEST = 0x0200_0000,
    /// Echo response.
    ECHO_RESPONSE = 0x0400_0000,
    /// Flow report (6 bytes).
    FLOW = 0x0800_0000,
}

impl PacketFlags {
    /// True when any bit of `flags` is set.
    #[must_use]
    pub const fn has(self, flags: Self) -> bool {
        self.0 & flags.0 != 0
    }

    /// The raw flags word.
    #[must_use]
    pub const fn bits(self) -> u32 {
        self.0
    }
}

impl BitOr for PacketFlags {
    type Output = Self;
    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}

impl BitOrAssign for PacketFlags {
    fn bitor_assign(&mut self, rhs: Self) {
        self.0 |= rhs.0;
    }
}

impl BitAnd for PacketFlags {
    type Output = Self;
    fn bitand(self, rhs: Self) -> Self {
        Self(self.0 & rhs.0)
    }
}

impl fmt::Debug for PacketFlags {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.0 == 0 {
            return f.write_str("NONE");
        }
        let mut rest = self.0;
        let mut first = true;
        for &(name, value) in Self::NAMES {
            if value != 0 && self.0 & value == value {
                if !first {
                    f.write_str(" | ")?;
                }
                f.write_str(name)?;
                first = false;
                rest &= !value;
            }
        }
        if rest != 0 {
            if !first {
                f.write_str(" | ")?;
            }
            write!(f, "{rest:#010x}")?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Source: OpenAC tests `Packets/PacketHeaderFlagsConformanceTests.cs` —
    // every flag value the reference server declares, and no others.
    #[test]
    fn flag_values_match_the_wire_protocol() {
        let expected: &[(&str, u32)] = &[
            ("NONE", 0x0000_0000),
            ("RETRANSMISSION", 0x0000_0001),
            ("ENCRYPTED_CHECKSUM", 0x0000_0002),
            ("BLOB_FRAGMENTS", 0x0000_0004),
            ("SERVER_SWITCH", 0x0000_0100),
            ("LOGON_SERVER_ADDR", 0x0000_0200),
            ("EMPTY_HEADER1", 0x0000_0400),
            ("REFERRAL", 0x0000_0800),
            ("REQUEST_RETRANSMIT", 0x0000_1000),
            ("REJECT_RETRANSMIT", 0x0000_2000),
            ("ACK_SEQUENCE", 0x0000_4000),
            ("DISCONNECT", 0x0000_8000),
            ("LOGIN_REQUEST", 0x0001_0000),
            ("WORLD_LOGIN_REQUEST", 0x0002_0000),
            ("CONNECT_REQUEST", 0x0004_0000),
            ("CONNECT_RESPONSE", 0x0008_0000),
            ("NET_ERROR", 0x0010_0000),
            ("NET_ERROR_DISCONNECT", 0x0020_0000),
            ("CICMD_COMMAND", 0x0040_0000),
            ("TIME_SYNC", 0x0100_0000),
            ("ECHO_REQUEST", 0x0200_0000),
            ("ECHO_RESPONSE", 0x0400_0000),
            ("FLOW", 0x0800_0000),
        ];
        assert_eq!(PacketFlags::NAMES, expected);
    }

    #[test]
    fn has_is_any_bit_equality_is_exact() {
        let f = PacketFlags::ENCRYPTED_CHECKSUM | PacketFlags::TIME_SYNC;
        assert!(f.has(PacketFlags::ENCRYPTED_CHECKSUM));
        assert!(f.has(PacketFlags::TIME_SYNC));
        assert!(!f.has(PacketFlags::LOGIN_REQUEST));
        assert_ne!(f, PacketFlags::TIME_SYNC);
        assert_eq!(
            format!("{f:?}"),
            "ENCRYPTED_CHECKSUM | TIME_SYNC".to_string()
        );
    }
}
