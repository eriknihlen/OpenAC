//! The squelch (ignore) database the server keeps: squelched accounts,
//! squelched characters with the message types squelched for each, and the
//! global squelch. The client holds a copy and replaces it whole.

use std::collections::{HashMap, HashSet};

/// One squelch: whose, how wide, and which message types.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SquelchInfo {
    /// The squelched name.
    pub name: String,
    /// True when the squelch covers the whole account.
    pub account_wide: bool,
    /// The squelched message types.
    pub message_types: HashSet<u32>,
}

/// The whole squelch database.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SquelchDatabase {
    /// Squelched accounts: account name to id.
    pub accounts: HashMap<String, u32>,
    /// Squelched characters by character id.
    pub characters: HashMap<u32, SquelchInfo>,
    /// The global squelch.
    pub global: SquelchInfo,
}

impl SquelchDatabase {
    /// The id of a squelched account, matching the name case-insensitively.
    #[must_use]
    pub fn account_id(&self, name: &str) -> Option<u32> {
        if let Some(&id) = self.accounts.get(name) {
            return Some(id);
        }
        let wanted: Vec<char> = name.chars().flat_map(char::to_uppercase).collect();
        self.accounts
            .iter()
            .find(|(candidate, _)| {
                candidate
                    .chars()
                    .flat_map(char::to_uppercase)
                    .eq(wanted.iter().copied())
            })
            .map(|(_, &id)| id)
    }
}

/// The client's copy of the squelch database.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SquelchState {
    database: SquelchDatabase,
    revision: u64,
}

impl SquelchState {
    /// An empty database.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Advances on every replace and clear.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The current database.
    #[must_use]
    pub fn database(&self) -> &SquelchDatabase {
        &self.database
    }

    /// Replaces the whole database.
    pub fn replace(&mut self, database: SquelchDatabase) {
        self.database = database;
        self.revision += 1;
    }

    /// Drops the database, as at the start of the next character's session.
    pub fn clear(&mut self) {
        self.database = SquelchDatabase::default();
        self.revision += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn info(name: &str, account_wide: bool, types: &[u32]) -> SquelchInfo {
        SquelchInfo {
            name: name.to_owned(),
            account_wide,
            message_types: types.iter().copied().collect(),
        }
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Social/SocialStateTests.cs
    #[test]
    fn clear_leaves_an_empty_database() {
        let mut state = SquelchState::new();
        state.replace(SquelchDatabase {
            accounts: HashMap::from([("Account".to_owned(), 1)]),
            characters: HashMap::from([(2, info("Character", false, &[3]))]),
            global: info("Global", true, &[4]),
        });

        state.clear();

        let database = state.database();
        assert!(database.accounts.is_empty());
        assert!(database.characters.is_empty());
        assert!(database.global.message_types.is_empty());
        assert_eq!(database.global.name, "");
        assert!(!database.global.account_wide);
        state.clear();
        assert!(state.database().accounts.is_empty());
    }

    #[test]
    fn replace_swaps_the_whole_database() {
        let mut state = SquelchState::new();
        let expected = SquelchDatabase {
            accounts: HashMap::from([("Account".to_owned(), 7)]),
            characters: HashMap::from([(42, info("Character", false, &[2]))]),
            global: info("", false, &[7]),
        };
        let before = state.revision();
        state.replace(expected.clone());
        assert_eq!(*state.database(), expected);
        assert!(state.revision() > before);
    }

    #[test]
    fn account_lookup_ignores_case() {
        let database = SquelchDatabase {
            accounts: HashMap::from([("Account".to_owned(), 7)]),
            ..SquelchDatabase::default()
        };
        assert_eq!(database.account_id("account"), Some(7));
        assert_eq!(database.account_id("ACCOUNT"), Some(7));
        assert_eq!(database.account_id("other"), None);
    }
}
