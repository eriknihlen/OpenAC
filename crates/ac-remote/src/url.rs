//! The relay's address: `wss://host[:port]/path`, or `ws://` (plain, for
//! local tests only). No user name, query or fragment: the token travels in
//! a header, never in the address.

use std::fmt;

/// A parsed relay address.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RelayUrl {
    /// `wss` (TLS) rather than `ws`.
    pub secure: bool,
    /// The host name or address, without IPv6 brackets.
    pub host: String,
    /// The port: as written, else 443 for `wss` and 80 for `ws`.
    pub port: u16,
    /// The path, starting with `/` (`/` when none was written).
    pub path: String,
}

/// Why an address is not a relay address. No variant carries the text.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum UrlError {
    /// Not `wss://` or `ws://`.
    #[error("the address does not start with wss:// or ws://")]
    Scheme,
    /// No host after the scheme.
    #[error("the address has no host")]
    NoHost,
    /// A host with characters a host name cannot have.
    #[error("the host is not a host name or address")]
    BadHost,
    /// The port is not 1 to 65535.
    #[error("the port is not 1 to 65535")]
    BadPort,
    /// A user name or password before the host.
    #[error("the address has a user name; the token goes in a header")]
    UserInfo,
    /// A `?query` or `#fragment`.
    #[error("the address has a query or fragment")]
    QueryOrFragment,
    /// The path has spaces or control characters.
    #[error("the path has spaces or control characters")]
    BadPath,
}

impl RelayUrl {
    /// Parses `text`.
    ///
    /// # Errors
    /// The text is not a `wss://` or `ws://` address as described above.
    pub fn parse(text: &str) -> Result<Self, UrlError> {
        let (secure, rest) = if let Some(rest) = strip_prefix_ignore_case(text, "wss://") {
            (true, rest)
        } else if let Some(rest) = strip_prefix_ignore_case(text, "ws://") {
            (false, rest)
        } else {
            return Err(UrlError::Scheme);
        };
        if rest.contains(['?', '#']) {
            return Err(UrlError::QueryOrFragment);
        }
        let (authority, path) = match rest.find('/') {
            Some(slash) => (&rest[..slash], &rest[slash..]),
            None => (rest, "/"),
        };
        if authority.contains('@') {
            return Err(UrlError::UserInfo);
        }
        if authority.is_empty() {
            return Err(UrlError::NoHost);
        }
        let (host, port) = if let Some(inner) = authority.strip_prefix('[') {
            let close = inner.find(']').ok_or(UrlError::BadHost)?;
            let host = &inner[..close];
            let after = &inner[close + 1..];
            let port = match after {
                "" => None,
                _ => Some(after.strip_prefix(':').ok_or(UrlError::BadHost)?),
            };
            if host.is_empty()
                || !host
                    .chars()
                    .all(|c| c.is_ascii_hexdigit() || c == ':' || c == '.')
            {
                return Err(UrlError::BadHost);
            }
            (host, port)
        } else {
            let (host, port) = match authority.rfind(':') {
                Some(colon) => (&authority[..colon], Some(&authority[colon + 1..])),
                None => (authority, None),
            };
            if host.is_empty() {
                return Err(UrlError::NoHost);
            }
            if !host
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '.' | '_'))
            {
                return Err(UrlError::BadHost);
            }
            (host, port)
        };
        let port = match port {
            None => {
                if secure {
                    443
                } else {
                    80
                }
            }
            Some(digits) => {
                if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
                    return Err(UrlError::BadPort);
                }
                digits
                    .parse::<u16>()
                    .ok()
                    .filter(|&p| p != 0)
                    .ok_or(UrlError::BadPort)?
            }
        };
        // The path goes into the request line as it is: printable ASCII only.
        if !path.bytes().all(|b| b.is_ascii_graphic()) {
            return Err(UrlError::BadPath);
        }
        Ok(Self {
            secure,
            host: host.to_owned(),
            port,
            path: path.to_owned(),
        })
    }

    /// The `Host` header's value: the host (IPv6 in brackets), with the port
    /// when it is not the scheme's default.
    #[must_use]
    pub fn host_header(&self) -> String {
        let host = if self.host.contains(':') {
            format!("[{}]", self.host)
        } else {
            self.host.clone()
        };
        let default = if self.secure { 443 } else { 80 };
        if self.port == default {
            host
        } else {
            format!("{host}:{}", self.port)
        }
    }
}

fn strip_prefix_ignore_case<'a>(text: &'a str, prefix: &str) -> Option<&'a str> {
    let head = text.get(..prefix.len())?;
    head.eq_ignore_ascii_case(prefix)
        .then(|| &text[prefix.len()..])
}

impl fmt::Display for RelayUrl {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "{}://{}{}",
            if self.secure { "wss" } else { "ws" },
            self.host_header(),
            self.path
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn url(secure: bool, host: &str, port: u16, path: &str) -> RelayUrl {
        RelayUrl {
            secure,
            host: host.to_owned(),
            port,
            path: path.to_owned(),
        }
    }

    #[test]
    fn parses_secure_and_plain_addresses() {
        assert_eq!(
            RelayUrl::parse("wss://relay.example/api/remote-client/ws"),
            Ok(url(true, "relay.example", 443, "/api/remote-client/ws"))
        );
        assert_eq!(
            RelayUrl::parse("ws://127.0.0.1:8797/client/ws"),
            Ok(url(false, "127.0.0.1", 8797, "/client/ws"))
        );
        assert_eq!(
            RelayUrl::parse("WSS://Relay.Example:8443"),
            Ok(url(true, "Relay.Example", 8443, "/"))
        );
        assert_eq!(
            RelayUrl::parse("ws://[::1]:9000/x"),
            Ok(url(false, "::1", 9000, "/x"))
        );
        assert_eq!(RelayUrl::parse("ws://h"), Ok(url(false, "h", 80, "/")));
    }

    #[test]
    fn host_header_names_a_port_only_when_not_the_default() {
        assert_eq!(
            RelayUrl::parse("wss://a.example/x").unwrap().host_header(),
            "a.example"
        );
        assert_eq!(
            RelayUrl::parse("wss://a.example:443/")
                .unwrap()
                .host_header(),
            "a.example"
        );
        assert_eq!(
            RelayUrl::parse("wss://a.example:80/")
                .unwrap()
                .host_header(),
            "a.example:80"
        );
        assert_eq!(
            RelayUrl::parse("ws://[::1]:81/").unwrap().host_header(),
            "[::1]:81"
        );
        assert_eq!(
            RelayUrl::parse("ws://127.0.0.1:8797/client/ws")
                .unwrap()
                .to_string(),
            "ws://127.0.0.1:8797/client/ws"
        );
    }

    #[test]
    fn refuses_what_is_not_a_relay_address() {
        let error = |text: &str| RelayUrl::parse(text).unwrap_err();
        assert_eq!(error("https://a.example/"), UrlError::Scheme);
        assert_eq!(error("a.example/x"), UrlError::Scheme);
        assert_eq!(error(""), UrlError::Scheme);
        assert_eq!(error("wss://"), UrlError::NoHost);
        assert_eq!(error("wss:///path"), UrlError::NoHost);
        assert_eq!(error("wss://:443/"), UrlError::NoHost);
        assert_eq!(error("wss://user:pw@a.example/"), UrlError::UserInfo);
        assert_eq!(error("wss://a@b/"), UrlError::UserInfo);
        assert_eq!(
            error("wss://a.example/x?token=1"),
            UrlError::QueryOrFragment
        );
        assert_eq!(error("wss://a.example/x#f"), UrlError::QueryOrFragment);
        assert_eq!(error("wss://a.example?x"), UrlError::QueryOrFragment);
        assert_eq!(error("wss://a.example:0/"), UrlError::BadPort);
        assert_eq!(error("wss://a.example:65536/"), UrlError::BadPort);
        assert_eq!(error("wss://a.example:/"), UrlError::BadPort);
        assert_eq!(error("wss://a.example:x1/"), UrlError::BadPort);
        assert_eq!(error("wss://a b/"), UrlError::BadHost);
        assert_eq!(error("wss://[::1/"), UrlError::BadHost);
        assert_eq!(error("wss://[::1]x/"), UrlError::BadHost);
        assert_eq!(error("wss://a.example/a b"), UrlError::BadPath);
        assert_eq!(error("wss://a.example/a\r\nX: y"), UrlError::BadPath);
    }

    #[test]
    fn errors_never_repeat_the_text() {
        let secret = "wss://hunter2@a.example/";
        let error = RelayUrl::parse(secret).unwrap_err();
        assert!(!format!("{error} {error:?}").contains("hunter2"));
    }
}
