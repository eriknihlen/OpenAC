//! Launcher profiles: servers, the accounts on each (with their plugins,
//! logon commands and profile tags), and the characters on each account,
//! kept in one versioned document (`launcher-profiles.v2.json` in the
//! settings folder; an older launcher's `launcher-profiles.json` is read
//! once and migrated, never written).
//!
//! Passwords are stored in that document in plain text, as OpenAC stores
//! them (a deliberate choice there); on Unix the file is kept at mode
//! 0600. Whether to move them to the operating system's credential store is
//! an open question.

mod account_name;
mod checked_accounts;
mod document;
mod migration;
mod store;
mod text;
pub mod thwarg;

pub use account_name::{find_account, same_account};
pub use checked_accounts::{
    CHECKED_ACCOUNTS_FILE_NAME, CheckedAccountsError, LauncherCheckedAccount,
    LauncherCheckedAccountStore,
};
pub use document::{
    AccountProfile, CharacterProfile, LaunchMode, LauncherProfileDocument, ServerProfile,
};
pub use store::{
    CURRENT_PROFILE_VERSION, CharacterRosterEntry, LauncherProfileStore, OLDER_PROFILE_FILE_NAME,
    PROFILE_FILE_NAME, PROFILE_VERSION_1, ProfileError,
};
pub use text::{LauncherTextEditorKind, apply_text, describe_account_removals, read_text};

/// Character ids as the launcher writes them: `0x` and eight uppercase hex
/// digits.
pub mod character_id {
    /// `0x` followed by the id as eight uppercase hex digits.
    pub fn to_hex_string(id: u32) -> String {
        format!("0x{id:08X}")
    }

    /// Reads an id written with a `0x` (or `0X`) prefix; hex digits of either
    /// case. Text without the prefix is refused, so a decimal id typed by
    /// hand is never read as hex.
    pub fn try_parse(text: Option<&str>) -> Option<u32> {
        let trimmed = text?.trim();
        if trimmed.is_empty() {
            return None;
        }
        let rest = trimmed
            .strip_prefix("0x")
            .or_else(|| trimmed.strip_prefix("0X"))?;
        // The hex number reader OpenAC uses allows white space around it.
        let digits = rest.trim();
        if digits.is_empty() || !digits.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return None;
        }
        u32::from_str_radix(digits, 16).ok()
    }
}

#[cfg(test)]
mod tests {
    use super::character_id::{to_hex_string, try_parse};

    // Ported from OpenAC tests/AcDream.Launcher.Core.Tests/Profiles/CharacterIdFormatTests.cs.

    #[test]
    fn to_hex_string_formats_eight_digit_uppercase_with_prefix() {
        assert_eq!(to_hex_string(0x5000_000A), "0x5000000A");
        assert_eq!(to_hex_string(1), "0x00000001");
    }

    #[test]
    fn try_parse_accepts_the_0x_prefix_case_insensitively() {
        for text in ["0x5000000A", "0x5000000a", "0X5000000A"] {
            assert_eq!(try_parse(Some(text)), Some(0x5000_000A), "{text}");
        }
    }

    #[test]
    fn try_parse_rejects_null_empty_non_hex_or_an_unprefixed_string() {
        for text in [
            None,
            Some(""),
            Some("   "),
            Some("not-hex"),
            Some("5000000A"),
            Some("12345678"),
        ] {
            assert_eq!(try_parse(text), None, "{text:?}");
        }
    }

    #[test]
    fn round_trips_through_to_hex_string_and_try_parse() {
        let original = 0x5000_000A;
        assert_eq!(try_parse(Some(&to_hex_string(original))), Some(original));
    }
}
