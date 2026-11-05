//! Absolute web addresses: just enough of RFC 3986 for the launcher's
//! checks (scheme, host, port, loopback, path segments, query, fragment,
//! user information) and for resolving a redirect's `Location`.

use std::fmt;

/// An absolute URL with a scheme and an authority, normalized the way
/// OpenAC's `System.Uri` normalizes web addresses: scheme and host in lower
/// case, the default port dropped, an empty path written as `/`, dot
/// segments removed.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct WebUrl {
    scheme: String,
    user_info: String,
    host: String,
    port: Option<u16>,
    path: String,
    query: String,
    fragment: String,
}

fn default_port(scheme: &str) -> Option<u16> {
    match scheme {
        "https" => Some(443),
        "http" => Some(80),
        _ => None,
    }
}

/// Percent-encodes everything but the unreserved characters (letters,
/// digits, `-`, `_`, `.`, `~`), as `Uri.EscapeDataString` does.
pub fn escape_data_string(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for byte in text.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.' | b'~') {
            out.push(char::from(byte));
        } else {
            out.push_str(&format!("%{byte:02X}"));
        }
    }
    out
}

/// Escapes characters a URL may not carry literally (non-ASCII as UTF-8
/// percent-escapes); refuses white space and control characters.
fn clean(text: &str) -> Option<String> {
    let mut out = String::with_capacity(text.len());
    for character in text.chars() {
        if character.is_control() || character == ' ' {
            return None;
        }
        if character.is_ascii() {
            out.push(character);
        } else {
            let mut buffer = [0u8; 4];
            for byte in character.encode_utf8(&mut buffer).bytes() {
                out.push_str(&format!("%{byte:02X}"));
            }
        }
    }
    Some(out)
}

/// RFC 3986 section 5.2.4.
fn remove_dot_segments(path: &str) -> String {
    let mut input = path;
    let mut output: Vec<&str> = Vec::new();
    let absolute = path.starts_with('/');
    let mut trailing_slash = false;
    if absolute {
        input = &input[1..];
    }
    for segment in input.split('/') {
        trailing_slash = false;
        match segment {
            "." => trailing_slash = true,
            ".." => {
                output.pop();
                trailing_slash = true;
            }
            other => output.push(other),
        }
    }
    let mut result = String::new();
    if absolute {
        result.push('/');
    }
    result.push_str(&output.join("/"));
    if trailing_slash && !result.ends_with('/') {
        result.push('/');
    }
    result
}

fn split_off<'a>(text: &mut &'a str, marker: char) -> Option<&'a str> {
    let index = text.find(marker)?;
    let tail = &text[index + 1..];
    *text = &text[..index];
    Some(tail)
}

impl WebUrl {
    /// Parses an absolute URL (`scheme://authority/path?query#fragment`).
    pub fn parse(text: &str) -> Option<Self> {
        let text = clean(text.trim())?;
        let (scheme, rest) = text.split_once("://")?;
        if scheme.is_empty()
            || !scheme.starts_with(|c: char| c.is_ascii_alphabetic())
            || !scheme
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '+' | '-' | '.'))
        {
            return None;
        }
        let scheme = scheme.to_ascii_lowercase();
        let mut rest = rest;
        let fragment = split_off(&mut rest, '#').unwrap_or_default().to_owned();
        let query = split_off(&mut rest, '?').unwrap_or_default().to_owned();
        let (authority, path) = match rest.find('/') {
            Some(index) => (&rest[..index], &rest[index..]),
            None => (rest, ""),
        };
        let (user_info, host_port) = match authority.rfind('@') {
            Some(index) => (&authority[..index], &authority[index + 1..]),
            None => ("", authority),
        };
        let (host, port_text) = if let Some(after) = host_port.strip_prefix('[') {
            let close = after.find(']')?;
            let host = &host_port[..close + 2];
            let tail = &after[close + 1..];
            let port = match tail {
                "" => None,
                tail => Some(tail.strip_prefix(':')?),
            };
            (host, port)
        } else {
            match host_port.rsplit_once(':') {
                Some((host, port)) => (host, Some(port)),
                None => (host_port, None),
            }
        };
        if host.is_empty() {
            return None;
        }
        let port = match port_text {
            None | Some("") => None,
            Some(port) => Some(port.parse::<u16>().ok()?),
        };
        let port = port.filter(|&port| Some(port) != default_port(&scheme));
        let path = if path.is_empty() {
            "/".to_owned()
        } else {
            remove_dot_segments(path)
        };
        Some(Self {
            scheme,
            user_info: user_info.to_owned(),
            host: host.to_ascii_lowercase(),
            port,
            path,
            query,
            fragment,
        })
    }

    /// Resolves a reference (absolute or relative, as a `Location` header
    /// carries) against this URL (RFC 3986 section 5.2.2).
    pub fn join(&self, reference: &str) -> Option<Self> {
        let reference = clean(reference.trim())?;
        if reference.contains("://") && Self::parse(&reference).is_some() {
            return Self::parse(&reference);
        }
        if let Some(network) = reference.strip_prefix("//") {
            return Self::parse(&format!("{}://{network}", self.scheme));
        }
        let mut rest = reference.as_str();
        let fragment = split_off(&mut rest, '#').map(str::to_owned);
        let query = split_off(&mut rest, '?').map(str::to_owned);
        let mut target = self.clone();
        target.fragment = fragment.unwrap_or_default();
        if rest.is_empty() {
            if let Some(query) = query {
                target.query = query;
            }
            return Some(target);
        }
        target.query = query.unwrap_or_default();
        target.path = if rest.starts_with('/') {
            remove_dot_segments(rest)
        } else {
            let base = match self.path.rfind('/') {
                Some(index) => &self.path[..=index],
                None => "/",
            };
            remove_dot_segments(&format!("{base}{rest}"))
        };
        Some(target)
    }

    /// The scheme, in lower case.
    pub fn scheme(&self) -> &str {
        &self.scheme
    }

    /// The host, in lower case (an IPv6 address keeps its brackets).
    pub fn host(&self) -> &str {
        &self.host
    }

    /// The port, the scheme's default when none was written.
    pub fn port(&self) -> Option<u16> {
        self.port.or_else(|| default_port(&self.scheme))
    }

    /// The path, starting with `/`, escapes kept.
    pub fn path(&self) -> &str {
        &self.path
    }

    /// The query without its `?` (empty when there is none).
    pub fn query(&self) -> &str {
        &self.query
    }

    /// The fragment without its `#` (empty when there is none).
    pub fn fragment(&self) -> &str {
        &self.fragment
    }

    /// The user information before `@` (empty when there is none).
    pub fn user_info(&self) -> &str {
        &self.user_info
    }

    /// The path's non-empty segments.
    pub fn segments(&self) -> Vec<&str> {
        self.path
            .split('/')
            .filter(|segment| !segment.is_empty())
            .collect()
    }

    /// Whether the scheme is `https`.
    pub fn is_https(&self) -> bool {
        self.scheme == "https"
    }

    /// Whether the scheme is `http`.
    pub fn is_http(&self) -> bool {
        self.scheme == "http"
    }

    /// Whether the host is this machine (`localhost`, `127.0.0.0/8`, `::1`).
    pub fn is_loopback(&self) -> bool {
        if self.host == "localhost" || self.host == "[::1]" {
            return true;
        }
        self.host
            .parse::<std::net::Ipv4Addr>()
            .is_ok_and(|address| address.is_loopback())
    }
}

impl fmt::Display for WebUrl {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}://", self.scheme)?;
        if !self.user_info.is_empty() {
            write!(f, "{}@", self.user_info)?;
        }
        f.write_str(&self.host)?;
        if let Some(port) = self.port {
            write!(f, ":{port}")?;
        }
        f.write_str(&self.path)?;
        if !self.query.is_empty() {
            write!(f, "?{}", self.query)?;
        }
        if !self.fragment.is_empty() {
            write!(f, "#{}", self.fragment)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_and_normalizes() {
        let url = WebUrl::parse("HTTPS://GitHub.com:443/owner/repo/").unwrap();
        assert_eq!(url.to_string(), "https://github.com/owner/repo/");
        assert_eq!(url.port(), Some(443));
        assert_eq!(url.segments(), ["owner", "repo"]);
        let url = WebUrl::parse("http://127.0.0.1:8080/a/./b/../c?x=1#f").unwrap();
        assert_eq!(url.path(), "/a/c");
        assert_eq!(url.query(), "x=1");
        assert_eq!(url.fragment(), "f");
        assert!(url.is_loopback());
        assert!(WebUrl::parse("http://[::1]:9/x").unwrap().is_loopback());
        assert!(!WebUrl::parse("https://example.org/").unwrap().is_loopback());
        assert_eq!(
            WebUrl::parse("https://user:pw@example.org/")
                .unwrap()
                .user_info(),
            "user:pw"
        );
        assert!(WebUrl::parse("relative/path").is_none());
        assert!(WebUrl::parse("https://exa mple.org/").is_none());
        assert!(WebUrl::parse("https://example.org:99999/").is_none());
    }

    #[test]
    fn joins_references() {
        let base =
            WebUrl::parse("https://github.com/o/r/releases/latest/download/plugin.json").unwrap();
        assert_eq!(
            base.join("/o/r/releases/download/v1.0.0/plugin.json")
                .unwrap()
                .to_string(),
            "https://github.com/o/r/releases/download/v1.0.0/plugin.json"
        );
        assert_eq!(
            base.join("../other.json").unwrap().to_string(),
            "https://github.com/o/r/releases/latest/other.json"
        );
        assert_eq!(
            base.join("//objects.example.org/x").unwrap().to_string(),
            "https://objects.example.org/x"
        );
        assert_eq!(
            base.join("http://other.example/y").unwrap().to_string(),
            "http://other.example/y"
        );
    }

    #[test]
    fn escapes_data_strings() {
        assert_eq!(escape_data_string("a b/c+d~e"), "a%20b%2Fc%2Bd~e");
        assert_eq!(escape_data_string("v1.0.0-beta.1"), "v1.0.0-beta.1");
    }
}
