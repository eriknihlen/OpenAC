use ac_bits::Reader;

use crate::{Opcode, Result, expect_opcode};

/// Why the server rejected a login or character-selection step.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum CharacterErrorCode {
    /// No specific reason.
    Undefined = 0x00,
    /// Generic logon failure.
    Logon = 0x01,
    /// The character is already logged on.
    LoggedOn = 0x02,
    /// The account is already logged on.
    AccountLogon = 0x03,
    /// The server crashed.
    ServerCrash = 0x04,
    /// Logged off.
    Logoff = 0x05,
    /// Character deletion failed.
    Delete = 0x06,
    /// No premade characters.
    NoPremade = 0x07,
    /// The account is in use.
    AccountInUse = 0x08,
    /// The account name is invalid.
    AccountInvalid = 0x09,
    /// The account does not exist.
    AccountDoesntExist = 0x0A,
    /// Entering the game failed.
    EnterGameGeneric = 0x0B,
    /// Entering the game failed: stress-test account.
    EnterGameStressAccount = 0x0C,
    /// Entering the game failed: the character is already in the world.
    EnterGameCharacterInWorld = 0x0D,
    /// Entering the game failed: the player account is missing.
    EnterGamePlayerAccountMissing = 0x0E,
    /// Entering the game failed: the account does not own the character.
    EnterGameCharacterNotOwned = 0x0F,
    /// Entering the game failed: the character is in the world on a server.
    EnterGameCharacterInWorldServer = 0x10,
    /// Entering the game failed: the character is too old.
    EnterGameOldCharacter = 0x11,
    /// Entering the game failed: the character is corrupt.
    EnterGameCorruptCharacter = 0x12,
    /// Entering the game failed: the start server is down.
    EnterGameStartServerDown = 0x13,
    /// Entering the game failed: the character could not be placed.
    EnterGameCouldntPlaceCharacter = 0x14,
    /// The server is full.
    LogonServerFull = 0x15,
    /// The character is booted.
    CharacterIsBooted = 0x16,
    /// Entering the game failed: the character is locked.
    EnterGameCharacterLocked = 0x17,
    /// The subscription has expired.
    SubscriptionExpired = 0x18,
    /// One past the last error code. The server sends it as a "no error"
    /// sentinel during enter world; the session ignores it.
    NumErrors = 0x19,
}

impl CharacterErrorCode {
    const ALL: [CharacterErrorCode; 26] = [
        Self::Undefined,
        Self::Logon,
        Self::LoggedOn,
        Self::AccountLogon,
        Self::ServerCrash,
        Self::Logoff,
        Self::Delete,
        Self::NoPremade,
        Self::AccountInUse,
        Self::AccountInvalid,
        Self::AccountDoesntExist,
        Self::EnterGameGeneric,
        Self::EnterGameStressAccount,
        Self::EnterGameCharacterInWorld,
        Self::EnterGamePlayerAccountMissing,
        Self::EnterGameCharacterNotOwned,
        Self::EnterGameCharacterInWorldServer,
        Self::EnterGameOldCharacter,
        Self::EnterGameCorruptCharacter,
        Self::EnterGameStartServerDown,
        Self::EnterGameCouldntPlaceCharacter,
        Self::LogonServerFull,
        Self::CharacterIsBooted,
        Self::EnterGameCharacterLocked,
        Self::SubscriptionExpired,
        Self::NumErrors,
    ];

    /// The wire value.
    #[must_use]
    pub const fn value(self) -> u32 {
        self as u32
    }

    /// The code with this wire value, if it is a known one.
    #[must_use]
    pub fn from_u32(value: u32) -> Option<Self> {
        Self::ALL.into_iter().find(|code| code.value() == value)
    }
}

/// A character error from the server ([`Opcode::CharacterError`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct CharacterError {
    /// The error code as sent; unknown values are kept, not rejected.
    pub raw_error_code: u32,
}

impl CharacterError {
    /// Parse a character-error body.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::CharacterError)?;
        Ok(Self {
            raw_error_code: r.u32()?,
        })
    }

    /// The known code, or `None` for a value outside the table.
    #[must_use]
    pub fn code(self) -> Option<CharacterErrorCode> {
        CharacterErrorCode::from_u32(self.raw_error_code)
    }

    /// True for the [`CharacterErrorCode::NumErrors`] sentinel, which the
    /// session ignores rather than treating as a rejection.
    #[must_use]
    pub fn is_sentinel(self) -> bool {
        self.code() == Some(CharacterErrorCode::NumErrors)
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/CharacterErrorTests.cs`.

    use super::*;
    use crate::ProtoError;
    use crate::test_util::{assert_every_prefix_fails, words};

    #[test]
    fn every_code_round_trips_raw_and_named_value() {
        for (raw, code) in (0u32..).zip(CharacterErrorCode::ALL) {
            let parsed = CharacterError::parse(&words(&[0xF659, raw])).unwrap();
            assert_eq!(parsed.raw_error_code, raw);
            assert_eq!(parsed.code(), Some(code));
            assert_eq!(code.value(), raw);
        }
        assert_eq!(CharacterErrorCode::NumErrors.value(), 0x19);
        assert_eq!(CharacterErrorCode::EnterGameCharacterInWorld.value(), 0x0D);
    }

    #[test]
    fn unknown_code_is_kept_raw() {
        for raw in [0xDEAD_BEEF, u32::MAX, 0x1A] {
            let parsed = CharacterError::parse(&words(&[0xF659, raw])).unwrap();
            assert_eq!(parsed.raw_error_code, raw);
            assert_eq!(parsed.code(), None);
        }
    }

    #[test]
    fn golden_bytes() {
        let body = [0x59, 0xF6, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00];
        assert_eq!(words(&[0xF659, CharacterErrorCode::Delete.value()]), body);
        let parsed = CharacterError::parse(&body).unwrap();
        assert_eq!(parsed.code(), Some(CharacterErrorCode::Delete));
        assert!(!parsed.is_sentinel());
    }

    #[test]
    fn num_errors_is_the_sentinel() {
        assert!(
            CharacterError::parse(&words(&[0xF659, 0x19]))
                .unwrap()
                .is_sentinel()
        );
    }

    #[test]
    fn wrong_opcode_truncated_and_empty_are_errors() {
        assert!(matches!(
            CharacterError::parse(&words(&[0xDEAD_BEEF])),
            Err(ProtoError::WrongOpcode { .. })
        ));
        assert_every_prefix_fails(&words(&[0xF659, 6]), CharacterError::parse);
    }
}
