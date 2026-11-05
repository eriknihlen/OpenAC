/// The message queue a game message travels on. Each fragment header
/// carries one; the server keeps per-queue ordering rules.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u16)]
pub enum GameMessageGroup {
    /// Not a real queue.
    InvalidQueue = 0x00,
    /// Game events.
    EventQueue = 0x01,
    /// Control messages (logoff, ...).
    ControlQueue = 0x02,
    /// Object (weenie) messages.
    WeenieQueue = 0x03,
    /// Login messages (character select, enter world).
    LoginQueue = 0x04,
    /// Data-file version checks.
    DatabaseQueue = 0x05,
    /// Secure control.
    SecureControlQueue = 0x06,
    /// Secure object messages.
    SecureWeenieQueue = 0x07,
    /// Secure login.
    SecureLoginQueue = 0x08,
    /// User-interface messages (the default for game actions).
    UiQueue = 0x09,
    /// World/physics messages.
    SmartboxQueue = 0x0A,
    /// Observer messages.
    ObserverQueue = 0x0B,
}

impl GameMessageGroup {
    /// The wire value.
    #[must_use]
    pub const fn to_u16(self) -> u16 {
        self as u16
    }

    /// From the wire value; `None` for values no queue uses.
    #[must_use]
    pub const fn from_u16(value: u16) -> Option<Self> {
        Some(match value {
            0x00 => Self::InvalidQueue,
            0x01 => Self::EventQueue,
            0x02 => Self::ControlQueue,
            0x03 => Self::WeenieQueue,
            0x04 => Self::LoginQueue,
            0x05 => Self::DatabaseQueue,
            0x06 => Self::SecureControlQueue,
            0x07 => Self::SecureWeenieQueue,
            0x08 => Self::SecureLoginQueue,
            0x09 => Self::UiQueue,
            0x0A => Self::SmartboxQueue,
            0x0B => Self::ObserverQueue,
            _ => return None,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wire_values_round_trip() {
        for value in 0..=0x0Bu16 {
            assert_eq!(GameMessageGroup::from_u16(value).unwrap().to_u16(), value);
        }
        assert_eq!(GameMessageGroup::from_u16(0x0C), None);
        assert_eq!(GameMessageGroup::UiQueue.to_u16(), 9);
    }
}
