//! Account names, which the server matches without regard to case: `Fefe`
//! and `fefe` log in to one account. Every launcher check of account
//! identity goes through [`same_account`]; names themselves are kept and
//! sent as they were typed. An account is always identified by its server
//! and its name; server names are compared exactly.

/// Whether `a` and `b` name the same account: equal once each is lowercased
/// character by character.
pub fn same_account(a: &str, b: &str) -> bool {
    a.chars()
        .flat_map(char::to_lowercase)
        .eq(b.chars().flat_map(char::to_lowercase))
}

/// The position of the entry for `name`: the one spelled exactly so, or else
/// the first in order that is the same account. A file saved before names
/// were compared this way can hold two spellings of one account; each still
/// finds its own entry.
pub fn find_account<T>(entries: &[T], name_of: impl Fn(&T) -> &str, name: &str) -> Option<usize> {
    entries
        .iter()
        .position(|entry| name_of(entry) == name)
        .or_else(|| {
            entries
                .iter()
                .position(|entry| same_account(name_of(entry), name))
        })
}

#[cfg(test)]
mod tests {
    use super::{find_account, same_account};

    #[test]
    fn names_that_differ_only_in_case_are_the_same_account() {
        assert!(same_account("Fefe", "fefe"));
        assert!(same_account("FEFE", "fefe"));
        assert!(same_account("fefe", "fefe"));
        assert!(same_account("Ärlig", "äRLIG"));
        assert!(!same_account("fefe", "fefe2"));
        assert!(!same_account("fefe", "fefe "));
        assert!(!same_account("", "fefe"));
    }

    #[test]
    fn find_prefers_the_exact_spelling_then_the_first_same_account() {
        let names = ["Fefe", "fefe"];
        let find = |name| find_account(&names, |entry| entry, name);
        assert_eq!(find("fefe"), Some(1));
        assert_eq!(find("Fefe"), Some(0));
        assert_eq!(find("FEFE"), Some(0));
        assert_eq!(find("other"), None);
    }
}
