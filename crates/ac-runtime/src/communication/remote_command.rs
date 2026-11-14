//! `/remoteac`: the player's switch for the session's remote window. The
//! session never sends it to the server, never shows the line, and keeps a
//! line that carries the secret out of the entry's history; the host that
//! owns the session's relay link carries it out.

use std::fmt;

/// The verb, without its `/` or `@`.
pub const VERB: &str = "remoteac";

/// What `/remoteac` (or `/remoteac help`) answers.
pub const HELP: &str = "\
/remoteac url \"<wss url>\"  set the relay address (wss:// anywhere, ws:// only to this computer)
/remoteac secret \"<token>\"  set the relay token (never shown again)
/remoteac enable  connect to the relay
/remoteac disable  leave the relay (a browser is let go; the url and secret are kept)
/remoteac status  whether it is on, the url, the link, and whether a browser is attached";

/// A relay token as typed. Its debug form never shows it, and its bytes are
/// overwritten when it is dropped.
#[derive(Clone, PartialEq, Eq)]
pub struct RemoteSecret(String);

impl RemoteSecret {
    /// The token.
    #[must_use]
    pub fn reveal(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for RemoteSecret {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("RemoteSecret(<hidden>)")
    }
}

impl Drop for RemoteSecret {
    fn drop(&mut self) {
        // Best effort: overwrite the bytes before the allocation is freed.
        let mut bytes = std::mem::take(&mut self.0).into_bytes();
        bytes.fill(0);
        std::hint::black_box(&bytes);
    }
}

/// A `/remoteac` request, for the host that owns the session's relay link.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RemoteCommand {
    /// Set the relay's address (as typed; the host checks it).
    Url(String),
    /// Set the relay token.
    Secret(RemoteSecret),
    /// Connect to the relay.
    Enable,
    /// Leave the relay.
    Disable,
    /// Say how the remote window stands.
    Status,
}

/// `line` as a `/remoteac` request: `None` when it is not one, `Some(Err)`
/// with the text to show when it is one with the wrong shape (or asks for
/// help).
#[must_use]
pub fn parse(line: &str) -> Option<Result<RemoteCommand, String>> {
    let rest = rest_of(line)?;
    let (word, argument) = match rest.find(char::is_whitespace) {
        Some(at) => (&rest[..at], rest[at..].trim()),
        None => (rest, ""),
    };
    let word = word.to_ascii_lowercase();
    let value = unquote(argument);
    let parsed = match (word.as_str(), value.is_empty()) {
        ("url", false) => Ok(RemoteCommand::Url(value.to_owned())),
        ("url", true) => Err(format!("/remoteac url needs an address.\n{HELP}")),
        ("secret", false) => Ok(RemoteCommand::Secret(RemoteSecret(value.to_owned()))),
        ("secret", true) => Err(format!("/remoteac secret needs a token.\n{HELP}")),
        ("enable" | "on", true) => Ok(RemoteCommand::Enable),
        ("disable" | "off", true) => Ok(RemoteCommand::Disable),
        ("status", true) => Ok(RemoteCommand::Status),
        ("" | "help", _) => Err(HELP.to_owned()),
        // The word is not repeated: it may be a token typed in the wrong
        // place.
        _ => Err(format!("That is not a /remoteac command.\n{HELP}")),
    };
    Some(parsed)
}

/// Whether `line` is kept out of the entry's history: a `/remoteac secret`
/// line, and any `/remoteac` line of the wrong shape (a mistyped `secret`
/// may still carry the token).
#[must_use]
pub fn kept_out_of_history(line: &str) -> bool {
    match parse(line) {
        None => false,
        Some(Ok(command)) => matches!(command, RemoteCommand::Secret(_)),
        Some(Err(_)) => true,
    }
}

/// The words a `/remoteac` line starts with that carry no token.
const PLAIN_WORDS: [&str; 7] = ["url", "enable", "on", "disable", "off", "status", "help"];

/// `draft` as the entry's views are shown it while it is typed: in a
/// `/remoteac` line, everything after the verb that may be the token is
/// starred out (an attached browser sees the draft as it is typed). The
/// typed line itself is unchanged.
#[must_use]
pub fn masked_draft(draft: &str) -> std::borrow::Cow<'_, str> {
    use std::borrow::Cow;
    let Some(rest) = rest_of(draft) else {
        return Cow::Borrowed(draft);
    };
    // Where `rest` starts in `draft` (it is a slice of it).
    let kept = (rest.as_ptr() as usize)
        .saturating_sub(draft.as_ptr() as usize)
        .min(draft.len());
    let word_end = rest.find(char::is_whitespace).unwrap_or(rest.len());
    let word = rest[..word_end].to_ascii_lowercase();
    // A word on its way to `secret` (or to a plain one) is shown; anything
    // else may be a token typed in the wrong place.
    let shown_word = !word.is_empty()
        && (PLAIN_WORDS
            .iter()
            .chain(["secret"].iter())
            .any(|w| w.starts_with(&word)));
    if !shown_word {
        return Cow::Owned(format!("{}{}", &draft[..kept], star(&draft[kept..])));
    }
    if PLAIN_WORDS.contains(&word.as_str()) || rest.len() == word_end {
        return Cow::Borrowed(draft);
    }
    let after_word = kept + word_end;
    Cow::Owned(format!(
        "{}{}",
        &draft[..after_word],
        star(&draft[after_word..])
    ))
}

/// `text` with every character but white space as `*`.
fn star(text: &str) -> String {
    text.chars()
        .map(|c| if c.is_whitespace() { c } else { '*' })
        .collect()
}

/// What follows the verb (trimmed), when `line` is `/remoteac` or
/// `@remoteac`.
fn rest_of(line: &str) -> Option<&str> {
    let line = line.trim();
    let after = line.strip_prefix('/').or_else(|| line.strip_prefix('@'))?;
    let verb_end = after.find(char::is_whitespace).unwrap_or(after.len());
    after[..verb_end]
        .eq_ignore_ascii_case(VERB)
        .then(|| after[verb_end..].trim())
}

/// `text` without one pair of surrounding double quotes.
fn unquote(text: &str) -> &str {
    text.strip_prefix('"')
        .and_then(|inner| inner.strip_suffix('"'))
        .map_or(text, str::trim)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_draft_shows_the_words_and_stars_the_token() {
        let cases = [
            ("hello there", "hello there"),
            ("/remoteac", "/remoteac"),
            ("/remoteac sec", "/remoteac sec"),
            ("/remoteac secret", "/remoteac secret"),
            ("/remoteac secret hun", "/remoteac secret ***"),
            ("/remoteac secret \"a b\"", "/remoteac secret ** **"),
            ("  @RemoteAC Secret x", "  @RemoteAC Secret *"),
            (
                "/remoteac url wss://relay.example/",
                "/remoteac url wss://relay.example/",
            ),
            ("/remoteac status", "/remoteac status"),
            ("/remoteac hunter2", "/remoteac *******"),
            ("/remoteac secrets hunter2", "/remoteac ******* *******"),
        ];
        for (draft, shown) in cases {
            assert_eq!(masked_draft(draft), shown, "{draft}");
        }
    }

    #[test]
    fn each_request_is_read_with_or_without_quotes() {
        assert_eq!(
            parse(r#"/remoteac url "wss://relay.example/ws""#),
            Some(Ok(RemoteCommand::Url("wss://relay.example/ws".into())))
        );
        assert_eq!(
            parse("/remoteac url wss://relay.example/ws"),
            Some(Ok(RemoteCommand::Url("wss://relay.example/ws".into())))
        );
        assert_eq!(
            parse(r#"@RemoteAC SECRET "a b c""#),
            Some(Ok(RemoteCommand::Secret(RemoteSecret("a b c".into()))))
        );
        assert_eq!(parse("/remoteac enable"), Some(Ok(RemoteCommand::Enable)));
        assert_eq!(parse("/remoteac disable"), Some(Ok(RemoteCommand::Disable)));
        assert_eq!(
            parse("  /remoteac status "),
            Some(Ok(RemoteCommand::Status))
        );
    }

    #[test]
    fn other_lines_are_not_remoteac() {
        assert_eq!(parse("/remoteacx status"), None);
        assert_eq!(parse("remoteac status"), None);
        assert_eq!(parse("/tell remoteac hi"), None);
        assert_eq!(parse("/remote status"), None);
    }

    #[test]
    fn a_wrong_shape_answers_with_the_help() {
        for line in [
            "/remoteac",
            "/remoteac help",
            "/remoteac url",
            "/remoteac secret",
            "/remoteac secret \"\"",
            "/remoteac enable now",
            "/remoteac frobnicate",
        ] {
            let answer = parse(line).unwrap().unwrap_err();
            assert!(answer.contains("/remoteac status"), "{line}: {answer}");
        }
    }

    #[test]
    fn the_secret_never_shows_in_its_debug_form_or_the_help() {
        let Some(Ok(command)) = parse("/remoteac secret hunter2-token") else {
            panic!("a secret line");
        };
        let debug = format!("{command:?}");
        assert!(!debug.contains("hunter2"), "{debug}");
        assert!(debug.contains("<hidden>"));
        // A token typed in the wrong place is not repeated back.
        for line in [
            "/remoteac secrets hunter2",
            "/remoteac hunter2",
            "/remoteac \"hunter2\"",
        ] {
            let answer = parse(line).unwrap().unwrap_err();
            assert!(!answer.contains("hunter2"), "{answer}");
        }
    }

    #[test]
    fn secret_and_mistyped_lines_are_kept_out_of_the_history() {
        assert!(kept_out_of_history("/remoteac secret x"));
        assert!(kept_out_of_history("@REMOTEAC Secret \"x\""));
        assert!(kept_out_of_history("/remoteac secert x"));
        assert!(kept_out_of_history("/remoteac secret"));
        assert!(!kept_out_of_history("/remoteac url wss://a"));
        assert!(!kept_out_of_history("/remoteac status"));
        assert!(!kept_out_of_history("/say remoteac secret x"));
    }
}
