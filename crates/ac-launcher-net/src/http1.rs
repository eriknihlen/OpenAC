//! HTTP/1.1 on a byte stream (RFC 9112): the one kind of request the
//! launcher sends, the response head, and the body's framing. No I/O of its
//! own beyond the reader it is given; every limit is explicit, and malformed
//! input is an error, never a panic.

use std::io::{self, BufRead, Read};

use ac_launcher_core::url::WebUrl;

/// The most a response head (status line and headers) may take.
pub(crate) const MAX_HEAD_BYTES: u64 = 64 * 1024;
/// The most header lines one response head may carry.
const MAX_HEADER_LINES: usize = 256;
/// The most a chunk-size line may take.
const MAX_CHUNK_LINE: u64 = 4096;
/// The most the trailer section after the last chunk may take.
const MAX_TRAILER_BYTES: u64 = 16 * 1024;
/// How many interim (1xx) responses may come before the final one.
const MAX_INTERIM_RESPONSES: usize = 16;
/// How many empty lines may come before a status line.
const MAX_LEADING_EMPTY_LINES: usize = 4;

/// The `Host` header value: the host, with the port when it is not the
/// scheme's default.
pub(crate) fn host_header(url: &WebUrl) -> String {
    match (url.scheme(), url.port()) {
        ("https", Some(443)) | ("http", Some(80)) | (_, None) => url.host().to_owned(),
        (_, Some(port)) => format!("{}:{port}", url.host()),
    }
}

/// The request target: the path and the query; a fragment is never sent.
pub(crate) fn request_target(url: &WebUrl) -> String {
    if url.query().is_empty() {
        url.path().to_owned()
    } else {
        format!("{}?{}", url.path(), url.query())
    }
}

fn is_sendable(text: &str) -> bool {
    !text.is_empty() && text.bytes().all(|byte| byte.is_ascii_graphic())
}

/// The bytes of a `GET` for `url`: no body, no cookies, no credentials, the
/// launcher's user agent, `identity` as the only acceptable encoding (sizes
/// and hashes are checked on the bytes as published), and a connection that
/// closes after the response.
pub(crate) fn request(url: &WebUrl, user_agent: &str) -> Result<Vec<u8>, String> {
    let target = request_target(url);
    let host = host_header(url);
    if !is_sendable(&target) || !is_sendable(&host) {
        return Err("The address cannot be sent as an HTTP request.".to_owned());
    }
    if user_agent.bytes().any(|byte| byte.is_ascii_control()) {
        return Err("The user agent cannot be sent as an HTTP header.".to_owned());
    }
    Ok(format!(
        "GET {target} HTTP/1.1\r\n\
         Host: {host}\r\n\
         User-Agent: {user_agent}\r\n\
         Accept: */*\r\n\
         Accept-Encoding: identity\r\n\
         Connection: close\r\n\
         \r\n"
    )
    .into_bytes())
}

/// Why a response head could not be read.
#[derive(Debug)]
pub(crate) enum HeadError {
    /// The stream failed.
    Io(io::Error),
    /// The bytes are not an HTTP/1.x response head; the text says why.
    Invalid(String),
}

impl From<HeadError> for io::Error {
    fn from(error: HeadError) -> Self {
        match error {
            HeadError::Io(error) => error,
            HeadError::Invalid(text) => io::Error::new(io::ErrorKind::InvalidData, text),
        }
    }
}

fn invalid(text: impl Into<String>) -> HeadError {
    HeadError::Invalid(text.into())
}

/// One line ending in LF (a CR before it is dropped), at most `limit` bytes
/// with its ending. `None` at a clean end of stream.
fn read_line(reader: &mut impl BufRead, limit: u64) -> Result<Option<Vec<u8>>, HeadError> {
    let mut line = Vec::new();
    let read = reader
        .by_ref()
        .take(limit)
        .read_until(b'\n', &mut line)
        .map_err(HeadError::Io)?;
    if read == 0 {
        return Ok(None);
    }
    if line.last() != Some(&b'\n') {
        return Err(if read as u64 >= limit {
            invalid("The server sent a line longer than the launcher accepts.")
        } else {
            invalid("The server closed the connection in the middle of a line.")
        });
    }
    line.pop();
    if line.last() == Some(&b'\r') {
        line.pop();
    }
    Ok(Some(line))
}

/// A response's status and headers (names in lower case, values trimmed,
/// in the order received).
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct ResponseHead {
    /// The status code.
    pub status: u16,
    /// The headers.
    pub headers: Vec<(String, String)>,
}

/// How the body after a head is delimited.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Framing {
    /// No body (204, 304).
    Empty,
    /// Exactly this many bytes.
    Length(u64),
    /// Chunked transfer coding.
    Chunked,
    /// Everything until the server closes the connection.
    UntilClose,
}

impl ResponseHead {
    /// Every value of the header `name` (lower case).
    pub(crate) fn values<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a str> + 'a {
        self.headers
            .iter()
            .filter(move |(header, _)| header == name)
            .map(|(_, value)| value.as_str())
    }

    fn list(&self, name: &str) -> Vec<String> {
        self.values(name)
            .flat_map(|value| value.split(','))
            .map(str::trim)
            .filter(|item| !item.is_empty())
            .map(str::to_owned)
            .collect()
    }

    /// The first `Location` header.
    pub(crate) fn location(&self) -> Option<String> {
        self.values("location").next().map(str::to_owned)
    }

    /// The content codings, as sent.
    pub(crate) fn content_encoding(&self) -> Vec<String> {
        self.list("content-encoding")
    }

    /// The declared length: every `Content-Length` value (repeated headers
    /// and comma lists included) must be the same decimal number.
    pub(crate) fn content_length(&self) -> Result<Option<u64>, String> {
        let mut length = None;
        for item in self
            .values("content-length")
            .flat_map(|value| value.split(','))
        {
            let item = item.trim();
            let parsed = if !item.is_empty() && item.bytes().all(|byte| byte.is_ascii_digit()) {
                item.parse::<u64>().ok()
            } else {
                None
            };
            let Some(parsed) = parsed else {
                return Err("The server sent an invalid Content-Length.".to_owned());
            };
            if length.is_some_and(|known| known != parsed) {
                return Err("The server sent conflicting Content-Length values.".to_owned());
            }
            length = Some(parsed);
        }
        Ok(length)
    }

    /// How the body is delimited (RFC 9112 section 6.3, for a `GET`): a
    /// transfer coding wins over a length, and the only coding decoded is
    /// `chunked` alone.
    pub(crate) fn framing(&self) -> Result<Framing, String> {
        if matches!(self.status, 204 | 304) {
            return Ok(Framing::Empty);
        }
        let codings: Vec<String> = self
            .list("transfer-encoding")
            .into_iter()
            .map(|coding| coding.to_ascii_lowercase())
            .collect();
        if !codings.is_empty() {
            return if codings == ["chunked"] {
                Ok(Framing::Chunked)
            } else {
                Err(format!(
                    "The server used a transfer coding the launcher does not decode ({}).",
                    codings.join(", ")
                ))
            };
        }
        Ok(match self.content_length()? {
            Some(length) => Framing::Length(length),
            None => Framing::UntilClose,
        })
    }
}

fn is_token_byte(byte: u8) -> bool {
    byte.is_ascii_alphanumeric() || b"!#$%&'*+-.^_`|~".contains(&byte)
}

fn parse_status_line(line: &[u8]) -> Result<u16, HeadError> {
    let not_http = || invalid("The server's answer is not an HTTP/1.x response.");
    let text = String::from_utf8_lossy(line);
    let (version, rest) = text.split_once(' ').ok_or_else(not_http)?;
    if version != "HTTP/1.1" && version != "HTTP/1.0" {
        return Err(not_http());
    }
    let code = rest.get(..3).ok_or_else(not_http)?;
    if !code.bytes().all(|byte| byte.is_ascii_digit())
        || !(rest.len() == 3 || rest.as_bytes()[3] == b' ')
    {
        return Err(not_http());
    }
    let status: u16 = code.parse().map_err(|_| not_http())?;
    if status < 100 {
        return Err(not_http());
    }
    Ok(status)
}

fn parse_header(line: &[u8]) -> Result<(String, String), HeadError> {
    if matches!(line.first(), Some(b' ' | b'\t')) {
        return Err(invalid(
            "The server folded a header line, which HTTP/1.1 no longer allows.",
        ));
    }
    let colon = line
        .iter()
        .position(|&byte| byte == b':')
        .ok_or_else(|| invalid("The server sent a header line without a colon."))?;
    let name = &line[..colon];
    if name.is_empty() || !name.iter().all(|&byte| is_token_byte(byte)) {
        return Err(invalid("The server sent an invalid header name."));
    }
    let value = String::from_utf8_lossy(&line[colon + 1..]);
    Ok((
        String::from_utf8_lossy(name).to_ascii_lowercase(),
        value.trim_matches([' ', '\t']).to_owned(),
    ))
}

/// Takes a line (and its ending) out of the head's byte budget.
fn take(line: &[u8], budget: &mut u64) -> Result<(), HeadError> {
    *budget = budget.saturating_sub(line.len() as u64 + 2);
    if *budget == 0 {
        Err(invalid(
            "The server's response head is larger than the launcher accepts.",
        ))
    } else {
        Ok(())
    }
}

fn read_one_head(reader: &mut impl BufRead) -> Result<ResponseHead, HeadError> {
    let mut budget = MAX_HEAD_BYTES;
    let mut empty_lines = 0;
    let status_line = loop {
        let line = read_line(reader, budget)?
            .ok_or_else(|| invalid("The server closed the connection without a response."))?;
        take(&line, &mut budget)?;
        if !line.is_empty() {
            break line;
        }
        empty_lines += 1;
        if empty_lines > MAX_LEADING_EMPTY_LINES {
            return Err(invalid("The server's answer is not an HTTP/1.x response."));
        }
    };
    let status = parse_status_line(&status_line)?;
    let mut headers = Vec::new();
    loop {
        let line = read_line(reader, budget)?.ok_or_else(|| {
            invalid("The server closed the connection in the middle of the response head.")
        })?;
        take(&line, &mut budget)?;
        if line.is_empty() {
            break;
        }
        if headers.len() == MAX_HEADER_LINES {
            return Err(invalid(
                "The server sent more header lines than the launcher accepts.",
            ));
        }
        headers.push(parse_header(&line)?);
    }
    Ok(ResponseHead { status, headers })
}

/// Reads the final response head, skipping interim (1xx) responses; a
/// protocol switch (101) was never asked for and is refused.
pub(crate) fn read_head(reader: &mut impl BufRead) -> Result<ResponseHead, HeadError> {
    for _ in 0..=MAX_INTERIM_RESPONSES {
        let head = read_one_head(reader)?;
        match head.status {
            101 => {
                return Err(invalid(
                    "The server switched protocols, which the launcher did not ask for.",
                ));
            }
            100..=199 => continue,
            _ => return Ok(head),
        }
    }
    Err(invalid(
        "The server sent more interim responses than the launcher accepts.",
    ))
}

fn parse_chunk_size(line: &[u8]) -> io::Result<u64> {
    let bad = || {
        io::Error::new(
            io::ErrorKind::InvalidData,
            "The server sent an invalid chunk size.",
        )
    };
    let size = match line.iter().position(|&byte| byte == b';') {
        Some(extensions) => &line[..extensions],
        None => line,
    };
    let size = size.trim_ascii();
    if size.is_empty() || !size.iter().all(u8::is_ascii_hexdigit) {
        return Err(bad());
    }
    let digits = match size.iter().position(|&byte| byte != b'0') {
        Some(first) => &size[first..],
        None => return Ok(0),
    };
    if digits.len() > 16 {
        return Err(bad());
    }
    let text = std::str::from_utf8(digits).map_err(|_| bad())?;
    u64::from_str_radix(text, 16).map_err(|_| bad())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum BodyState {
    Done,
    Length(u64),
    UntilClose,
    ChunkSize,
    ChunkData(u64),
    ChunkEnd,
    Trailers(u64),
}

/// A response body: the bytes of its framing, decoded from chunks when
/// chunked, ending where the framing says it ends. A body that ends early
/// is an error, not a short body.
#[derive(Debug)]
pub(crate) struct BodyReader<R> {
    inner: R,
    state: BodyState,
}

impl<R: BufRead> BodyReader<R> {
    /// The body after a head with `framing`.
    pub(crate) fn new(inner: R, framing: Framing) -> Self {
        let state = match framing {
            Framing::Empty | Framing::Length(0) => BodyState::Done,
            Framing::Length(length) => BodyState::Length(length),
            Framing::Chunked => BodyState::ChunkSize,
            Framing::UntilClose => BodyState::UntilClose,
        };
        Self { inner, state }
    }

    /// The stream underneath.
    #[cfg(test)]
    pub(crate) fn into_inner(self) -> R {
        self.inner
    }

    fn read_counted(&mut self, buffer: &mut [u8], remaining: u64) -> io::Result<usize> {
        let wanted = usize::try_from(remaining)
            .unwrap_or(usize::MAX)
            .min(buffer.len());
        let read = self.inner.read(&mut buffer[..wanted])?;
        if read == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                format!(
                    "The response body ended {remaining} bytes before the length the server declared."
                ),
            ));
        }
        Ok(read)
    }

    fn line(&mut self, limit: u64) -> io::Result<Vec<u8>> {
        read_line(&mut self.inner, limit)?.ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "The server closed the connection in the middle of a chunked body.",
            )
        })
    }
}

impl<R: BufRead> Read for BodyReader<R> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if buffer.is_empty() {
            return Ok(0);
        }
        loop {
            match self.state {
                BodyState::Done => return Ok(0),
                BodyState::UntilClose => {
                    let read = self.inner.read(buffer)?;
                    if read == 0 {
                        self.state = BodyState::Done;
                    }
                    return Ok(read);
                }
                BodyState::Length(remaining) => {
                    let read = self.read_counted(buffer, remaining)?;
                    let left = remaining - read as u64;
                    self.state = if left == 0 {
                        BodyState::Done
                    } else {
                        BodyState::Length(left)
                    };
                    return Ok(read);
                }
                BodyState::ChunkSize => {
                    let line = self.line(MAX_CHUNK_LINE)?;
                    self.state = match parse_chunk_size(&line)? {
                        0 => BodyState::Trailers(MAX_TRAILER_BYTES),
                        size => BodyState::ChunkData(size),
                    };
                }
                BodyState::ChunkData(remaining) => {
                    let read = self.read_counted(buffer, remaining)?;
                    let left = remaining - read as u64;
                    self.state = if left == 0 {
                        BodyState::ChunkEnd
                    } else {
                        BodyState::ChunkData(left)
                    };
                    return Ok(read);
                }
                BodyState::ChunkEnd => {
                    if !self.line(2)?.is_empty() {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "The server's chunk is longer than its declared size.",
                        ));
                    }
                    self.state = BodyState::ChunkSize;
                }
                BodyState::Trailers(budget) => {
                    let line = self.line(budget)?;
                    if line.is_empty() {
                        self.state = BodyState::Done;
                        continue;
                    }
                    let left = budget.saturating_sub(line.len() as u64 + 2);
                    if left == 0 {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "The server's trailers are larger than the launcher accepts.",
                        ));
                    }
                    self.state = BodyState::Trailers(left);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    fn url(text: &str) -> WebUrl {
        WebUrl::parse(text).unwrap()
    }

    fn head(text: &str) -> Result<ResponseHead, HeadError> {
        read_head(&mut Cursor::new(text.as_bytes().to_vec()))
    }

    fn body(text: &[u8], framing: Framing) -> io::Result<Vec<u8>> {
        let mut reader = BodyReader::new(Cursor::new(text.to_vec()), framing);
        let mut out = Vec::new();
        reader.read_to_end(&mut out)?;
        Ok(out)
    }

    #[test]
    fn the_request_is_a_plain_get_without_compression_or_keep_alive() {
        let bytes = request(
            &url("https://github.com/a/b/releases/latest/download/x.json?q=1#frag"),
            "OpenAC-launcher/1",
        )
        .unwrap();
        assert_eq!(
            String::from_utf8(bytes).unwrap(),
            "GET /a/b/releases/latest/download/x.json?q=1 HTTP/1.1\r\n\
             Host: github.com\r\n\
             User-Agent: OpenAC-launcher/1\r\n\
             Accept: */*\r\n\
             Accept-Encoding: identity\r\n\
             Connection: close\r\n\r\n"
        );
    }

    #[test]
    fn the_host_header_names_a_port_only_when_it_is_not_the_default() {
        assert_eq!(
            host_header(&url("https://example.test:443/")),
            "example.test"
        );
        assert_eq!(host_header(&url("http://127.0.0.1:80/")), "127.0.0.1");
        assert_eq!(
            host_header(&url("http://127.0.0.1:8080/")),
            "127.0.0.1:8080"
        );
        assert_eq!(host_header(&url("https://[::1]:8443/")), "[::1]:8443");
    }

    #[test]
    fn reads_status_and_headers_and_skips_interim_responses() {
        let parsed = head(
            "\r\nHTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 302 Found\r\nLocation:  /next \r\n\
             Content-Encoding: gzip, br\r\ncontent-length: 0\r\n\r\nrest",
        )
        .unwrap();
        assert_eq!(parsed.status, 302);
        assert_eq!(parsed.location().as_deref(), Some("/next"));
        assert_eq!(parsed.content_encoding(), ["gzip", "br"]);
        assert_eq!(parsed.content_length(), Ok(Some(0)));
        // A bare LF ends lines too.
        assert_eq!(head("HTTP/1.0 200\n\n").unwrap().status, 200);
    }

    #[test]
    fn refuses_what_is_not_an_http_response_head() {
        for text in [
            "",
            "SSH-2.0-OpenSSH\r\n\r\n",
            "HTTP/2 200 OK\r\n\r\n",
            "HTTP/1.1 20 OK\r\n\r\n",
            "HTTP/1.1 2000 OK\r\n\r\n",
            "HTTP/1.1 200 OK\r\nBad Header: x\r\n\r\n",
            "HTTP/1.1 200 OK\r\nNoColon\r\n\r\n",
            "HTTP/1.1 200 OK\r\nA: b\r\n folded\r\n\r\n",
            "HTTP/1.1 200 OK\r\nA: b\r\n",
            "HTTP/1.1 101 Switching Protocols\r\n\r\n",
        ] {
            assert!(head(text).is_err(), "{text:?}");
        }
        let huge = format!("HTTP/1.1 200 OK\r\nA: {}\r\n\r\n", "x".repeat(70_000));
        assert!(head(&huge).is_err());
        let many = format!("HTTP/1.1 200 OK\r\n{}\r\n", "A: b\r\n".repeat(300));
        assert!(head(&many).is_err());
    }

    #[test]
    fn framing_follows_rfc_9112() {
        let framing = |text: &str| head(text).unwrap().framing();
        assert_eq!(
            framing("HTTP/1.1 200 OK\r\nContent-Length: 12\r\n\r\n"),
            Ok(Framing::Length(12))
        );
        assert_eq!(
            framing("HTTP/1.1 200 OK\r\nContent-Length: 5, 5\r\nContent-Length: 5\r\n\r\n"),
            Ok(Framing::Length(5))
        );
        assert_eq!(
            framing("HTTP/1.1 200 OK\r\nTransfer-Encoding: Chunked\r\nContent-Length: 3\r\n\r\n"),
            Ok(Framing::Chunked)
        );
        assert_eq!(framing("HTTP/1.1 200 OK\r\n\r\n"), Ok(Framing::UntilClose));
        assert_eq!(
            framing("HTTP/1.1 304 Not Modified\r\nContent-Length: 9\r\n\r\n"),
            Ok(Framing::Empty)
        );
        for bad in [
            "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 99999999999999999999999\r\n\r\n",
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n",
        ] {
            assert!(framing(bad).is_err(), "{bad:?}");
        }
    }

    #[test]
    fn reads_exactly_the_declared_length_and_refuses_a_short_body() {
        assert_eq!(body(b"hello world", Framing::Length(5)).unwrap(), b"hello");
        let error = body(b"hel", Framing::Length(5)).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::UnexpectedEof);
        assert_eq!(body(b"ignored", Framing::Empty).unwrap(), b"");
        assert_eq!(
            body(b"all of it", Framing::UntilClose).unwrap(),
            b"all of it"
        );
    }

    #[test]
    fn decodes_chunks_with_extensions_and_trailers() {
        let encoded = b"4;name=value\r\nWiki\r\n5\r\npedia\r\n000E\r\n in\r\n\r\nchunks.\r\n0\r\nX-Trailer: 1\r\n\r\nAFTER";
        let mut reader = BodyReader::new(Cursor::new(encoded.to_vec()), Framing::Chunked);
        let mut out = Vec::new();
        reader.read_to_end(&mut out).unwrap();
        assert_eq!(out, b"Wikipedia in\r\n\r\nchunks.");
        // The reader stops at the end of the body, not the stream.
        let mut rest = Vec::new();
        reader.into_inner().read_to_end(&mut rest).unwrap();
        assert_eq!(rest, b"AFTER");
    }

    #[test]
    fn refuses_broken_chunked_bodies() {
        for encoded in [
            &b"4\r\nWik"[..],
            b"4\r\nWikipedia\r\n0\r\n\r\n",
            b"z\r\nWiki\r\n0\r\n\r\n",
            b"10000000000000000\r\n",
            b"4\r\nWiki\r\n",
            b"0\r\nX: 1\r\n",
        ] {
            assert!(
                body(encoded, Framing::Chunked).is_err(),
                "{}",
                String::from_utf8_lossy(encoded)
            );
        }
        assert_eq!(parse_chunk_size(b"00000000000000000001").unwrap(), 1);
        assert_eq!(parse_chunk_size(b"ffffffffffffffff").unwrap(), u64::MAX);
    }
}
