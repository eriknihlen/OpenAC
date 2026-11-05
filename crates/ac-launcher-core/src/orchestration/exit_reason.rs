//! Why a client ended, in words a player understands: from the client's
//! own exit code, the reason it reported, and the server's words when a
//! refusal from the server ended the session. The exit code stays as a
//! small suffix for whoever reads the session log.

use ac_host_core::ExitCode;

/// What the launcher knows about how a client ended.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct ClientExit<'a> {
    /// The process's exit code, once known.
    pub code: Option<i32>,
    /// The reason the client reported with its own exit; `None` when it
    /// ended without reporting one (a crash, a kill).
    pub reported_reason: Option<&'a str>,
    /// The server's own words, when a refusal from the server ended the
    /// session.
    pub server_message: Option<&'a str>,
}

/// The client's reason when the connection dropped and a window was
/// attached (it does not log in again then).
const CONNECTION_LOST: &str = "the connection to the server was lost";
/// How the client's reason starts when no character matched the launch.
const NO_CHARACTER: &str = "no available character";

/// The sentence a failed client exit shows, with ` (exit code N)` when the
/// code is known. A graceful exit (code 0) has no failure to show and
/// gets the plain end text.
pub fn describe_client_exit(exit: ClientExit<'_>) -> String {
    let text = exit_sentence(exit);
    match exit.code {
        Some(code) => format!("{text} (exit code {code})"),
        None => text,
    }
}

fn exit_sentence(exit: ClientExit<'_>) -> String {
    if let Some(message) = exit.server_message.map(str::trim).filter(|m| !m.is_empty()) {
        return message.to_owned();
    }
    let Some(reason) = exit.reported_reason.map(str::trim) else {
        // The process ended without saying why: it crashed or was killed.
        return "The client closed unexpectedly.".to_owned();
    };
    let code = exit.code.and_then(|code| u8::try_from(code).ok());
    let is = |known: ExitCode| code == Some(known.code());
    if is(ExitCode::Success) {
        return "The client logged out and closed.".to_owned();
    }
    if reason.eq_ignore_ascii_case(CONNECTION_LOST) {
        return "The connection to the server was lost.".to_owned();
    }
    if is(ExitCode::Connection) {
        return if reason.starts_with(NO_CHARACTER) {
            "The chosen character is not on this account.".to_owned()
        } else {
            "The client could not connect to the server.".to_owned()
        };
    }
    if is(ExitCode::Credential) {
        return "The client did not receive the account's password.".to_owned();
    }
    if is(ExitCode::Configuration) {
        return "The client could not use its session settings.".to_owned();
    }
    if is(ExitCode::Usage) {
        return "The client did not understand how it was started.".to_owned();
    }
    if reason.is_empty() {
        "The session ended with an error.".to_owned()
    } else {
        format!("The session ended with an error: {reason}.")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn exit<'a>(
        code: Option<i32>,
        reported_reason: Option<&'a str>,
        server_message: Option<&'a str>,
    ) -> String {
        describe_client_exit(ClientExit {
            code,
            reported_reason,
            server_message,
        })
    }

    #[test]
    fn a_crash_and_a_kill_read_as_an_unexpected_close() {
        assert_eq!(
            exit(Some(-1), None, None),
            "The client closed unexpectedly. (exit code -1)"
        );
        assert_eq!(exit(None, None, None), "The client closed unexpectedly.");
    }

    #[test]
    fn a_server_refusal_shows_the_servers_own_words() {
        assert_eq!(
            exit(
                Some(6),
                Some(
                    "the server ended the session: Another account is already logged on from \
                     this client. (error 0x1)"
                ),
                Some("Another account is already logged on from this client."),
            ),
            "Another account is already logged on from this client. (exit code 6)"
        );
    }

    #[test]
    fn the_clients_own_exit_codes_are_named() {
        assert_eq!(
            exit(Some(6), Some("the connection to the server was lost"), None),
            "The connection to the server was lost. (exit code 6)"
        );
        assert_eq!(
            exit(Some(5), Some("connect timed out: connect request"), None),
            "The client could not connect to the server. (exit code 5)"
        );
        assert_eq!(
            exit(
                Some(5),
                Some("no available character matches Name(\"Bob\")"),
                None
            ),
            "The chosen character is not on this account. (exit code 5)"
        );
        assert_eq!(
            exit(Some(4), Some("credential-error"), None),
            "The client did not receive the account's password. (exit code 4)"
        );
        assert_eq!(
            exit(Some(3), Some("configuration-error"), None),
            "The client could not use its session settings. (exit code 3)"
        );
        assert_eq!(
            exit(Some(6), Some("enter world timed out"), None),
            "The session ended with an error: enter world timed out. (exit code 6)"
        );
    }
}
