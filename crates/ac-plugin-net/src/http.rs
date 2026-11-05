//! HTTP/1.1 for plugins (RFC 9112): the address, the request's bytes, and
//! the response read off a connection (status line, headers, a body framed
//! by `Content-Length`, chunks or the connection's end), every limit
//! explicit and malformed input an error, never a panic.

use std::io::{self, BufRead, BufReader, Read};

use crate::link::Link;

/// The most a response head may take.
const MAX_HEAD_BYTES: usize = 64 * 1024;
/// The most header lines a head may carry.
const MAX_HEADERS: usize = 256;

/// An `http(s)://host[:port]/path?query` address.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Address {
    /// TLS.
    pub https: bool,
    /// The host, lower case (an IPv6 address without brackets).
    pub host: String,
    /// The port.
    pub port: u16,
    /// The path and query sent (`/` at least).
    pub target: String,
}

impl Address {
    /// Parses an address; `None` for anything else (credentials, another
    /// scheme, no host, a bad port, spaces or control characters).
    #[must_use]
    pub fn parse(url: &str) -> Option<Self> {
        let (https, rest) = if let Some(rest) = strip_prefix_ci(url, "https://") {
            (true, rest)
        } else {
            (false, strip_prefix_ci(url, "http://")?)
        };
        if url.bytes().any(|b| b <= b' ' || b == 0x7F) {
            return None;
        }
        let end = rest.find(['/', '?', '#']).unwrap_or(rest.len());
        let (authority, tail) = rest.split_at(end);
        if authority.is_empty() || authority.contains('@') {
            return None;
        }
        let (host, port) = if let Some(inner) = authority.strip_prefix('[') {
            let close = inner.find(']')?;
            let after = &inner[close + 1..];
            let port = match after.strip_prefix(':') {
                Some(p) => p.parse().ok()?,
                None if after.is_empty() => default_port(https),
                None => return None,
            };
            (inner[..close].to_owned(), port)
        } else {
            match authority.rsplit_once(':') {
                Some((h, p)) => (h.to_owned(), p.parse().ok()?),
                None => (authority.to_owned(), default_port(https)),
            }
        };
        if host.is_empty() || port == 0 {
            return None;
        }
        let tail = tail.split('#').next().unwrap_or("");
        let target = if tail.is_empty() {
            "/".to_owned()
        } else if tail.starts_with('?') {
            format!("/{tail}")
        } else {
            tail.to_owned()
        };
        Some(Self {
            https,
            host: host.to_lowercase(),
            port,
            target,
        })
    }

    /// The `Host` header: the host, with the port when not the default.
    fn host_header(&self) -> String {
        let host = if self.host.contains(':') {
            format!("[{}]", self.host)
        } else {
            self.host.clone()
        };
        if self.port == default_port(self.https) {
            host
        } else {
            format!("{host}:{}", self.port)
        }
    }

    /// Resolves a `Location` against this address.
    #[must_use]
    pub fn join(&self, location: &str) -> Option<Self> {
        if strip_prefix_ci(location, "https://").is_some()
            || strip_prefix_ci(location, "http://").is_some()
        {
            return Self::parse(location);
        }
        if location.starts_with("//") {
            let scheme = if self.https { "https:" } else { "http:" };
            return Self::parse(&format!("{scheme}{location}"));
        }
        let path = if location.starts_with('/') {
            location.to_owned()
        } else {
            let base = self.target.split('?').next().unwrap_or("/");
            let dir = &base[..=base.rfind('/').unwrap_or(0)];
            format!("{dir}{location}")
        };
        let scheme = if self.https { "https" } else { "http" };
        Self::parse(&format!("{scheme}://{}{path}", self.host_header()))
    }
}

fn strip_prefix_ci<'a>(text: &'a str, prefix: &str) -> Option<&'a str> {
    (text.len() >= prefix.len() && text[..prefix.len()].eq_ignore_ascii_case(prefix))
        .then(|| &text[prefix.len()..])
}

fn default_port(https: bool) -> u16 {
    if https { 443 } else { 80 }
}

fn is_token(text: &str) -> bool {
    !text.is_empty()
        && text
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"!#$%&'*+-.^_`|~".contains(&b))
}

/// The bytes of a request: the plugin's method and headers (a header the
/// client sets itself is left out), the body with its length, a connection
/// that closes after the response. `None` when the method or a header
/// cannot be sent.
#[must_use]
pub fn request_bytes(
    method: &str,
    address: &Address,
    headers: &[(String, String)],
    body: &[u8],
) -> Option<Vec<u8>> {
    build_request(method, address, headers, body, None)
}

/// The bytes of a request sent to an HTTP proxy for a plain `http`
/// address (RFC 9112 3.2.2): the whole address as the target, the
/// proxy's credentials when it asked, and a connection left open, as
/// .NET's handler sends it, so an NTLM or Negotiate handshake can run on it.
#[must_use]
pub(crate) fn proxy_request_bytes(
    method: &str,
    address: &Address,
    headers: &[(String, String)],
    body: &[u8],
    authorization: Option<&str>,
) -> Option<Vec<u8>> {
    build_request(method, address, headers, body, Some(authorization))
}

fn build_request(
    method: &str,
    address: &Address,
    headers: &[(String, String)],
    body: &[u8],
    proxy: Option<Option<&str>>,
) -> Option<Vec<u8>> {
    if !is_token(method) {
        return None;
    }
    let target = match proxy {
        Some(_) => format!("http://{}{}", address.host_header(), address.target),
        None => address.target.clone(),
    };
    let mut head = format!(
        "{method} {target} HTTP/1.1\r\nHost: {}\r\n",
        address.host_header()
    );
    for (name, value) in headers {
        if !is_token(name) || value.bytes().any(|b| b == b'\r' || b == b'\n' || b == 0) {
            return None;
        }
        let own = ["host", "content-length", "connection", "transfer-encoding"];
        if own.iter().any(|o| name.eq_ignore_ascii_case(o))
            || (matches!(proxy, Some(Some(_))) && name.eq_ignore_ascii_case("proxy-authorization"))
        {
            continue;
        }
        head.push_str(&format!("{name}: {value}\r\n"));
    }
    if let Some(Some(value)) = proxy {
        if value.bytes().any(|b| b.is_ascii_control()) {
            return None;
        }
        head.push_str(&format!("Proxy-Authorization: {value}\r\n"));
    }
    if !body.is_empty() || method.eq_ignore_ascii_case("POST") || method.eq_ignore_ascii_case("PUT")
    {
        head.push_str(&format!("Content-Length: {}\r\n", body.len()));
    }
    if proxy.is_none() {
        head.push_str("Connection: close\r\n");
    }
    head.push_str("\r\n");
    let mut bytes = head.into_bytes();
    bytes.extend_from_slice(body);
    Some(bytes)
}

/// The `User-Agent` among a request's headers, which .NET copies into a
/// tunnel's `CONNECT`.
pub(crate) fn user_agent(headers: &[(String, String)]) -> Option<&str> {
    headers
        .iter()
        .find(|(name, _)| name.eq_ignore_ascii_case("user-agent"))
        .map(|(_, value)| value.as_str())
        .filter(|value| !value.bytes().any(|b| b.is_ascii_control()))
}

/// A response read off a connection.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Response {
    /// The status code.
    pub status: u16,
    /// The reason phrase.
    pub reason: String,
    /// The headers, in order.
    pub headers: Vec<(String, String)>,
    /// The body.
    pub body: Vec<u8>,
}

impl Response {
    /// A header's value (the first), any case.
    #[must_use]
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(n, _)| n.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }
}

/// Why a response could not be read.
#[derive(Debug)]
pub enum ReadError {
    /// The connection failed (or timed out).
    Io(io::Error),
    /// Not HTTP the client reads; the text says why.
    Protocol(String),
    /// The body was larger than allowed.
    TooLarge,
}

impl From<io::Error> for ReadError {
    fn from(e: io::Error) -> Self {
        Self::Io(e)
    }
}

fn line(reader: &mut impl BufRead, budget: &mut usize) -> Result<String, ReadError> {
    let mut raw = Vec::new();
    let limit = u64::try_from(*budget).unwrap_or(u64::MAX) + 1;
    reader.take(limit).read_until(b'\n', &mut raw)?;
    if raw.len() > *budget {
        return Err(ReadError::Protocol("the response head is too large".into()));
    }
    *budget -= raw.len();
    if raw.last() != Some(&b'\n') {
        return Err(ReadError::Protocol(
            "the response ended inside its head".into(),
        ));
    }
    raw.pop();
    if raw.last() == Some(&b'\r') {
        raw.pop();
    }
    String::from_utf8(raw).map_err(|_| ReadError::Protocol("a header is not text".into()))
}

/// Reads one response (interim 1xx responses skipped) off `reader`, its
/// body at most `max_bytes`; `head_only` for a `HEAD` request. With the
/// response, whether the connection can carry another request (HTTP/1.1
/// or a kept-alive 1.0, not closing, the body framed by its length).
pub(crate) fn read_response(
    reader: &mut BufReader<Link>,
    max_bytes: usize,
    head_only: bool,
) -> Result<(Response, bool), ReadError> {
    let mut budget = MAX_HEAD_BYTES;
    let mut minor_version = 1;
    let (status, reason, headers) = loop {
        let status_line = line(reader, &mut budget)?;
        let mut parts = status_line.splitn(3, ' ');
        let version = parts.next().unwrap_or("");
        if !version.starts_with("HTTP/1.") {
            return Err(ReadError::Protocol("not an HTTP/1.x response".into()));
        }
        if version == "HTTP/1.0" {
            minor_version = 0;
        }
        let status: u16 = parts
            .next()
            .and_then(|s| s.parse().ok())
            .filter(|s| (100..=999).contains(s))
            .ok_or_else(|| ReadError::Protocol("the status code is not a number".into()))?;
        let reason = parts.next().unwrap_or("").to_owned();
        let mut headers = Vec::new();
        loop {
            let text = line(reader, &mut budget)?;
            if text.is_empty() {
                break;
            }
            if headers.len() == MAX_HEADERS {
                return Err(ReadError::Protocol("too many headers".into()));
            }
            let (name, value) = text
                .split_once(':')
                .ok_or_else(|| ReadError::Protocol("a header has no colon".into()))?;
            headers.push((name.trim().to_owned(), value.trim().to_owned()));
        }
        if (100..200).contains(&status) && status != 101 {
            continue;
        }
        break (status, reason, headers);
    };
    let mut response = Response {
        status,
        reason,
        headers,
        body: Vec::new(),
    };
    let has_token = |name: &str, token: &str| {
        response
            .headers
            .iter()
            .filter(|(n, _)| n.eq_ignore_ascii_case(name))
            .flat_map(|(_, v)| v.split(','))
            .any(|t| t.trim().eq_ignore_ascii_case(token))
    };
    let persistent = !has_token("connection", "close")
        && (minor_version == 1 || has_token("connection", "keep-alive"));
    if head_only || status == 204 || status == 304 {
        return Ok((response, persistent));
    }
    let chunked = response
        .header("transfer-encoding")
        .is_some_and(|v| v.to_ascii_lowercase().contains("chunked"));
    if chunked {
        loop {
            let mut chunk_budget = 4096;
            let size_line = line(reader, &mut chunk_budget)?;
            let size_text = size_line.split(';').next().unwrap_or("").trim();
            let size = usize::from_str_radix(size_text, 16)
                .map_err(|_| ReadError::Protocol("a chunk size is not a number".into()))?;
            if size == 0 {
                // Trailers, up to the empty line.
                let mut trailer_budget = 16 * 1024;
                while !line(reader, &mut trailer_budget)?.is_empty() {}
                break;
            }
            if response.body.len() + size > max_bytes {
                return Err(ReadError::TooLarge);
            }
            let start = response.body.len();
            response.body.resize(start + size, 0);
            reader.read_exact(&mut response.body[start..])?;
            let mut crlf_budget = 2;
            if !line(reader, &mut crlf_budget)?.is_empty() {
                return Err(ReadError::Protocol("a chunk does not end its line".into()));
            }
        }
    } else if let Some(length) = response.header("content-length") {
        let length: usize = length
            .parse()
            .map_err(|_| ReadError::Protocol("the length is not a number".into()))?;
        if length > max_bytes {
            return Err(ReadError::TooLarge);
        }
        response.body.resize(length, 0);
        reader.read_exact(&mut response.body)?;
    } else {
        let limit = u64::try_from(max_bytes).unwrap_or(u64::MAX) + 1;
        reader
            .by_ref()
            .take(limit)
            .read_to_end(&mut response.body)?;
        if response.body.len() > max_bytes {
            return Err(ReadError::TooLarge);
        }
        // The body ran until the connection closed.
        return Ok((response, false));
    }
    Ok((response, persistent))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn addresses_parse_and_redirects_resolve() {
        let a = Address::parse("https://Example.org/x/y?z=1#frag").unwrap();
        assert_eq!(
            (a.https, a.host.as_str(), a.port, a.target.as_str()),
            (true, "example.org", 443, "/x/y?z=1")
        );
        let b = Address::parse("http://localhost:8080").unwrap();
        assert_eq!((b.port, b.target.as_str()), (8080, "/"));
        let v6 = Address::parse("http://[::1]:9/").unwrap();
        assert_eq!(v6.host, "::1");
        for bad in [
            "ftp://x/",
            "https://user@x/",
            "https:///",
            "https://x:0/",
            "https://x/a b",
        ] {
            assert!(Address::parse(bad).is_none(), "{bad}");
        }
        assert_eq!(a.join("/top").unwrap().target, "/top");
        assert_eq!(a.join("sib").unwrap().target, "/x/sib");
        assert_eq!(
            a.join("https://cdn.example.net/f").unwrap().host,
            "cdn.example.net"
        );
        assert_eq!(a.join("//other.org/p").unwrap().host, "other.org");
    }

    #[test]
    fn a_request_keeps_the_clients_own_headers() {
        let a = Address::parse("http://h:81/p").unwrap();
        let bytes = request_bytes(
            "POST",
            &a,
            &[
                ("X-Plugin-Secret".into(), "s".into()),
                ("Host".into(), "evil".into()),
            ],
            b"body",
        )
        .unwrap();
        assert_eq!(
            String::from_utf8(bytes).unwrap(),
            "POST /p HTTP/1.1\r\nHost: h:81\r\nX-Plugin-Secret: s\r\nContent-Length: 4\r\nConnection: close\r\n\r\nbody"
        );
        assert!(request_bytes("GE T", &a, &[], b"").is_none());
        assert!(request_bytes("GET", &a, &[("A".into(), "x\r\nB: y".into())], b"").is_none());
    }
}
