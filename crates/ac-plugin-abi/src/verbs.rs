//! The rules for plugin chat verbs, shared by the client's registry and
//! the SDK's fake host so both refuse the same names.

/// The longest verb, in characters.
pub const MAX_VERB_LEN: usize = 32;

/// The refusal for a malformed verb.
pub const MALFORMED_VERB: &str = "Plugin command verbs must contain 1-32 letters or digits.";

/// The verb a plugin asked for, without a leading `/` or `@` and outer
/// blanks; refused unless it is 1 to 32 letters or digits.
///
/// # Errors
/// [`MALFORMED_VERB`].
pub fn normalize_verb(verb: &str) -> Result<String, String> {
    let normalized = verb.trim().trim_start_matches(['/', '@']);
    let length = normalized.chars().count();
    if !(1..=MAX_VERB_LEN).contains(&length) || !normalized.chars().all(char::is_alphanumeric) {
        return Err(MALFORMED_VERB.to_owned());
    }
    Ok(normalized.to_owned())
}

/// The refusal for a verb that is taken.
#[must_use]
pub fn already_registered(verb: &str) -> String {
    format!("Plugin command '{verb}' is already registered.")
}

/// A typed line split into its verb and arguments: `/verb args` or
/// `@verb args`, outer blanks trimmed; `None` for anything else.
#[must_use]
pub fn split_command_line(raw: &str) -> Option<(&str, &str, &str)> {
    let trimmed = raw.trim();
    let mut chars = trimmed.chars();
    if !matches!(chars.next(), Some('/' | '@')) || trimmed.len() < 2 {
        return None;
    }
    let body = &trimmed[1..];
    let (verb, arguments) = match body.find([' ', '\t']) {
        Some(at) => (&body[..at], body[at + 1..].trim()),
        None => (body, ""),
    };
    Some((trimmed, verb, arguments))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn verbs_are_one_to_thirty_two_letters_or_digits() {
        assert_eq!(normalize_verb(" /vt "), Ok("vt".to_owned()));
        assert_eq!(normalize_verb("@Go2"), Ok("Go2".to_owned()));
        assert_eq!(normalize_verb(&"a".repeat(32)), Ok("a".repeat(32)));
        for bad in ["", "/", "a b", "a-b", "x".repeat(33).as_str()] {
            assert_eq!(normalize_verb(bad), Err(MALFORMED_VERB.to_owned()), "{bad}");
        }
    }

    // OpenAC `PluginCommandRegistryTests.RegisteredVerbHandlesBothPrefixesCaseInsensitively`:
    // both prefixes, outer blanks trimmed, arguments trimmed.
    #[test]
    fn command_lines_split_on_the_first_blank() {
        assert_eq!(
            split_command_line(" @vt   opt get EnableCombat "),
            Some(("@vt   opt get EnableCombat", "vt", "opt get EnableCombat"))
        );
        assert_eq!(
            split_command_line("/VT start"),
            Some(("/VT start", "VT", "start"))
        );
        assert_eq!(split_command_line("/vt"), Some(("/vt", "vt", "")));
        assert_eq!(split_command_line("hello"), None);
        assert_eq!(split_command_line("/"), None);
    }
}
