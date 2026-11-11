//! A WebSocket client (RFC 6455), client side only, over any `Read + Write`
//! stream: the opening handshake, then text, binary, ping, pong and close
//! frames. No extensions and no subprotocols.
//!
//! It works on a stream whose reads time out often (a non-blocking socket,
//! or one with a short read timeout): every byte read goes into its own
//! inbound buffer and frames are parsed out of that, so a read that answers
//! `WouldBlock` or `TimedOut` in the middle of a frame loses nothing.
//! Everything the server sends is checked; anything invalid is a typed
//! [`WsError`], never a panic.

use std::io::{self, Read, Write};
use std::time::{Duration, Instant};

use crate::secret::Secret;

/// The value RFC 6455 appends to the key before hashing it.
const ACCEPT_GUID: &str = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

/// The longest response header the handshake reads.
pub const MAX_RESPONSE_HEADER: usize = 16 * 1024;

/// The `Sec-WebSocket-Accept` value a server answers `key` with.
#[must_use]
pub fn accept_key(key: &str) -> String {
    let mut text = String::with_capacity(key.len() + ACCEPT_GUID.len());
    text.push_str(key);
    text.push_str(ACCEPT_GUID);
    crate::base64::encode(&crate::sha1::digest(text.as_bytes()))
}

/// Opcodes (RFC 6455 section 5.2).
const CONTINUATION: u8 = 0x0;
const TEXT: u8 = 0x1;
const BINARY: u8 = 0x2;
const CLOSE: u8 = 0x8;
const PING: u8 = 0x9;
const PONG: u8 = 0xA;

/// How much one read asks the stream for.
const READ_CHUNK: usize = 16 * 1024;

/// A pause between reads while the handshake waits for a stream that
/// answers `WouldBlock` (a waiting stream sleeps itself and never does).
const HANDSHAKE_PAUSE: Duration = Duration::from_millis(1);

fn io_text(error: &io::Error) -> String {
    error.kind().to_string()
}

/// Printable ASCII without spaces: safe inside a header or request line.
fn header_safe(text: &str) -> bool {
    !text.is_empty() && text.bytes().all(|b| b.is_ascii_graphic())
}

/// A close code a peer may send (RFC 6455 section 7.4, with the codes
/// registered since: 1012 to 1014).
fn sendable_close_code(code: u16) -> bool {
    matches!(code, 1000..=1003 | 1007..=1014 | 3000..=4999)
}

/// Reads the server's answer up to the end of its header: the header text
/// and the bytes read after it.
fn read_answer<S: Read>(
    stream: &mut S,
    deadline: Instant,
) -> Result<(String, Vec<u8>), HandshakeError> {
    let mut buffer = Vec::with_capacity(1024);
    let mut chunk = [0u8; 1024];
    loop {
        match stream.read(&mut chunk) {
            Ok(0) => return Err(HandshakeError::Closed),
            Ok(n) => {
                let searched_from = buffer.len().saturating_sub(3);
                buffer.extend_from_slice(&chunk[..n]);
                if let Some(at) = buffer[searched_from..]
                    .windows(4)
                    .position(|w| w == b"\r\n\r\n")
                {
                    let end = searched_from + at + 4;
                    if end > MAX_RESPONSE_HEADER {
                        return Err(HandshakeError::HeaderTooLarge);
                    }
                    let leftover = buffer.split_off(end);
                    let head = String::from_utf8(buffer).map_err(|_| HandshakeError::Malformed)?;
                    return Ok((head, leftover));
                }
                if buffer.len() > MAX_RESPONSE_HEADER {
                    return Err(HandshakeError::HeaderTooLarge);
                }
            }
            Err(e)
                if matches!(
                    e.kind(),
                    io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut
                ) =>
            {
                if Instant::now() >= deadline {
                    return Err(HandshakeError::Timeout);
                }
                std::thread::sleep(HANDSHAKE_PAUSE);
            }
            Err(e) if e.kind() == io::ErrorKind::Interrupted => {}
            Err(e) => return Err(HandshakeError::Io(io_text(&e))),
        }
    }
}

/// Checks the answer's header for an upgrade that accepted `key`.
fn check_answer(head: &str, key: &str) -> Result<(), HandshakeError> {
    let mut lines = head.split("\r\n");
    let status = lines.next().unwrap_or_default();
    let code = status
        .strip_prefix("HTTP/1.1 ")
        .or_else(|| status.strip_prefix("HTTP/1.0 "))
        .and_then(|rest| rest.get(..3))
        .filter(|digits| digits.bytes().all(|b| b.is_ascii_digit()))
        .and_then(|digits| digits.parse::<u16>().ok())
        .ok_or(HandshakeError::Malformed)?;
    let mut upgrade = false;
    let mut connection = false;
    let mut accept = None;
    let mut extension = false;
    for line in lines.filter(|line| !line.is_empty()) {
        let (name, value) = line.split_once(':').ok_or(HandshakeError::Malformed)?;
        let value = value.trim();
        if name.eq_ignore_ascii_case("upgrade") {
            upgrade |= value.eq_ignore_ascii_case("websocket");
        } else if name.eq_ignore_ascii_case("connection") {
            connection |= value
                .split(',')
                .any(|token| token.trim().eq_ignore_ascii_case("upgrade"));
        } else if name.eq_ignore_ascii_case("sec-websocket-accept") {
            accept = Some(value);
        } else if name.eq_ignore_ascii_case("sec-websocket-extensions")
            || name.eq_ignore_ascii_case("sec-websocket-protocol")
        {
            extension = true;
        }
    }
    if code != 101 {
        return Err(HandshakeError::Status(code));
    }
    if !upgrade || !connection {
        return Err(HandshakeError::NotUpgraded);
    }
    if accept != Some(accept_key(key).as_str()) {
        return Err(HandshakeError::BadAccept);
    }
    if extension {
        return Err(HandshakeError::Extension);
    }
    Ok(())
}

/// Size limits, in payload bytes of one message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WsLimits {
    /// The longest text message accepted.
    pub max_text_in: usize,
    /// The longest binary message accepted.
    pub max_binary_in: usize,
    /// The longest text message sent.
    pub max_text_out: usize,
    /// The longest binary message sent.
    pub max_binary_out: usize,
}

impl Default for WsLimits {
    /// The relay's limits: 64 KiB in each direction, binary out 4 MiB.
    fn default() -> Self {
        Self {
            max_text_in: 64 * 1024,
            max_binary_in: 64 * 1024,
            max_text_out: 64 * 1024,
            max_binary_out: 4 * 1024 * 1024,
        }
    }
}

/// The opening request.
#[derive(Debug, Clone, Copy)]
pub struct Handshake<'a> {
    /// The `Host` header's value.
    pub host: &'a str,
    /// The request path, starting with `/`.
    pub path: &'a str,
    /// Sent as `Authorization: Bearer <token>` when present.
    pub bearer: Option<&'a Secret>,
}

/// Why the opening handshake failed. No variant carries text the server
/// sent or the request's headers.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum HandshakeError {
    /// The host, path or token has characters a header cannot carry.
    #[error("the request has characters a header cannot carry")]
    BadRequest,
    /// The stream failed.
    #[error("the connection failed during the handshake: {0}")]
    Io(String),
    /// The server closed the connection before answering.
    #[error("the server closed the connection during the handshake")]
    Closed,
    /// No complete answer before the deadline.
    #[error("the server did not answer the handshake in time")]
    Timeout,
    /// The answer's header is longer than [`MAX_RESPONSE_HEADER`].
    #[error("the server's answer header is too long")]
    HeaderTooLarge,
    /// The answer is not HTTP.
    #[error("the server's answer is not HTTP")]
    Malformed,
    /// The server answered with this status instead of 101 (401: the token
    /// was refused).
    #[error("the server answered HTTP {0} instead of switching to WebSocket")]
    Status(u16),
    /// 101, but without `Upgrade: websocket` and `Connection: Upgrade`.
    #[error("the server's answer does not upgrade to WebSocket")]
    NotUpgraded,
    /// `Sec-WebSocket-Accept` is missing or does not match our key.
    #[error("the server's Sec-WebSocket-Accept does not match the key")]
    BadAccept,
    /// The server chose an extension or subprotocol nobody asked for.
    #[error("the server chose an extension or subprotocol that was not offered")]
    Extension,
}

/// Why the connection cannot go on. No variant carries payload text.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum WsError {
    /// The stream failed.
    #[error("the connection failed: {0}")]
    Io(String),
    /// The server closed the connection without a close frame.
    #[error("the server closed the connection")]
    Closed,
    /// A close frame was already exchanged.
    #[error("the WebSocket is closed")]
    AlreadyClosed,
    /// A frame sets reserved bits (no extension was agreed).
    #[error("a frame sets reserved bits")]
    ReservedBits,
    /// A server frame is masked.
    #[error("a frame from the server is masked")]
    MaskedFrame,
    /// An opcode RFC 6455 does not define.
    #[error("a frame has the unknown opcode {0}")]
    UnknownOpcode(u8),
    /// A control frame without the final bit.
    #[error("a control frame is fragmented")]
    FragmentedControl,
    /// A control frame with more than 125 payload bytes.
    #[error("a control frame is longer than 125 bytes")]
    ControlTooLong,
    /// A length not written in its shortest form.
    #[error("a frame length is not in its shortest form")]
    NonMinimalLength,
    /// A message longer than the limit.
    #[error("a message is longer than {limit} bytes")]
    TooBig {
        /// The limit.
        limit: usize,
    },
    /// A continuation frame with no message to continue.
    #[error("a continuation frame continues nothing")]
    UnexpectedContinuation,
    /// A new message before the fragmented one ended.
    #[error("a new message started inside a fragmented one")]
    InterleavedMessage,
    /// A text message that is not UTF-8.
    #[error("a text message is not UTF-8")]
    InvalidUtf8,
    /// A close frame with one byte, a code that may not be sent, or a reason
    /// that is not UTF-8.
    #[error("a close frame is not valid")]
    BadClose,
    /// A message to send is longer than the limit.
    #[error("a message to send is {len} bytes, more than {limit}")]
    OutgoingTooBig {
        /// Its length.
        len: usize,
        /// The limit.
        limit: usize,
    },
}

/// What arrived.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WsEvent {
    /// A complete text message.
    Text(String),
    /// A complete binary message.
    Binary(Vec<u8>),
    /// A ping (already answered with a pong).
    Ping,
    /// A pong.
    Pong,
    /// The server closed (already answered); its code and reason if it gave
    /// them. Nothing more arrives.
    Close(Option<(u16, String)>),
}

/// A WebSocket client over `S`.
#[derive(Debug)]
pub struct WsClient<S> {
    stream: S,
    limits: WsLimits,
    /// Bytes read and not parsed yet, from `start`.
    inbound: Vec<u8>,
    start: usize,
    /// A fragmented message so far: whether it is text, and its bytes.
    partial: Option<(bool, Vec<u8>)>,
    masks: Masks,
    close_sent: bool,
    close_received: bool,
}

impl<S: Read + Write> WsClient<S> {
    /// Runs the opening handshake on `stream`; the server must answer
    /// before `deadline`.
    ///
    /// # Errors
    /// The request cannot be written, or the server's answer is not a
    /// WebSocket upgrade for our key.
    pub fn handshake(
        stream: S,
        request: &Handshake<'_>,
        limits: WsLimits,
        deadline: Instant,
    ) -> Result<Self, HandshakeError> {
        let mut stream = stream;
        let bearer = request.bearer.map(Secret::expose);
        if !header_safe(request.host)
            || !request.path.starts_with('/')
            || !header_safe(request.path)
            || bearer.is_some_and(|token| !header_safe(token))
        {
            return Err(HandshakeError::BadRequest);
        }
        let mut nonce = [0u8; 16];
        nonce[..8].copy_from_slice(&crate::protocol::random_u64().to_le_bytes());
        nonce[8..].copy_from_slice(&crate::protocol::random_u64().to_le_bytes());
        let key = crate::base64::encode(&nonce);
        let mut text = format!(
            "GET {} HTTP/1.1\r\nHost: {}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n",
            request.path, request.host
        );
        if let Some(token) = bearer {
            text.push_str("Authorization: Bearer ");
            text.push_str(token);
            text.push_str("\r\n");
        }
        text.push_str("\r\n");
        stream
            .write_all(text.as_bytes())
            .and_then(|()| stream.flush())
            .map_err(|e| HandshakeError::Io(io_text(&e)))?;
        drop(text);
        let (head, leftover) = read_answer(&mut stream, deadline)?;
        check_answer(&head, &key)?;
        Ok(Self::upgraded(stream, limits, leftover))
    }

    /// A client over a stream that is already upgraded, `leftover` the bytes
    /// read after the answer's header.
    fn upgraded(stream: S, limits: WsLimits, leftover: Vec<u8>) -> Self {
        Self {
            stream,
            limits,
            inbound: leftover,
            start: 0,
            partial: None,
            masks: Masks::new(),
            close_sent: false,
            close_received: false,
        }
    }

    /// The next event, reading from the stream until one is complete or the
    /// stream has nothing more right now (`None`).
    ///
    /// # Errors
    /// The stream failed or ended, or the server broke the protocol.
    pub fn read_event(&mut self) -> Result<Option<WsEvent>, WsError> {
        if self.close_received {
            return Err(WsError::AlreadyClosed);
        }
        loop {
            if let Some(event) = self.parse_buffered()? {
                return Ok(Some(event));
            }
            if !self.fill()? {
                return Ok(None);
            }
        }
    }

    /// Reads once into the inbound buffer: whether bytes came.
    fn fill(&mut self) -> Result<bool, WsError> {
        let mut chunk = [0u8; READ_CHUNK];
        loop {
            match self.stream.read(&mut chunk) {
                Ok(0) => return Err(WsError::Closed),
                Ok(n) => {
                    if self.start == self.inbound.len() {
                        self.inbound.clear();
                        self.start = 0;
                    } else if self.start >= READ_CHUNK * 4 {
                        self.inbound.drain(..self.start);
                        self.start = 0;
                    }
                    self.inbound.extend_from_slice(&chunk[..n]);
                    return Ok(true);
                }
                Err(e)
                    if matches!(
                        e.kind(),
                        io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut
                    ) =>
                {
                    return Ok(false);
                }
                Err(e) if e.kind() == io::ErrorKind::Interrupted => {}
                Err(e) => return Err(WsError::Io(io_text(&e))),
            }
        }
    }

    /// Parses frames out of the inbound buffer until one completes an event
    /// or a frame is not all there yet.
    fn parse_buffered(&mut self) -> Result<Option<WsEvent>, WsError> {
        loop {
            let buffer = &self.inbound[self.start..];
            let (Some(&first), Some(&second)) = (buffer.first(), buffer.get(1)) else {
                return Ok(None);
            };
            let fin = first & 0x80 != 0;
            if first & 0x70 != 0 {
                return Err(WsError::ReservedBits);
            }
            let opcode = first & 0x0F;
            if second & 0x80 != 0 {
                return Err(WsError::MaskedFrame);
            }
            let (length, header) = match second & 0x7F {
                126 => {
                    let Some(bytes) = buffer.get(2..4) else {
                        return Ok(None);
                    };
                    let length = u64::from(u16::from_be_bytes([bytes[0], bytes[1]]));
                    if length < 126 {
                        return Err(WsError::NonMinimalLength);
                    }
                    (length, 4)
                }
                127 => {
                    let Some(bytes) = buffer.get(2..10) else {
                        return Ok(None);
                    };
                    let mut be = [0u8; 8];
                    be.copy_from_slice(bytes);
                    let length = u64::from_be_bytes(be);
                    if length >> 63 != 0 || length <= 0xFFFF {
                        return Err(WsError::NonMinimalLength);
                    }
                    (length, 10)
                }
                short => (u64::from(short), 2),
            };
            let control = match opcode {
                CONTINUATION | TEXT | BINARY => false,
                CLOSE | PING | PONG => true,
                other => return Err(WsError::UnknownOpcode(other)),
            };
            if control {
                if !fin {
                    return Err(WsError::FragmentedControl);
                }
                if length > 125 {
                    return Err(WsError::ControlTooLong);
                }
            } else {
                let text = match (opcode, &self.partial) {
                    (CONTINUATION, None) => return Err(WsError::UnexpectedContinuation),
                    (CONTINUATION, Some((text, _))) => *text,
                    (_, Some(_)) => return Err(WsError::InterleavedMessage),
                    (kind, None) => kind == TEXT,
                };
                let limit = if text {
                    self.limits.max_text_in
                } else {
                    self.limits.max_binary_in
                };
                let so_far = self.partial.as_ref().map_or(0, |(_, bytes)| bytes.len());
                let total = usize::try_from(length)
                    .ok()
                    .and_then(|length| length.checked_add(so_far));
                if total.is_none_or(|total| total > limit) {
                    return Err(WsError::TooBig { limit });
                }
            }
            // Bounded above: a control frame is at most 125 bytes and a data
            // frame at most the limit.
            let length = usize::try_from(length).map_err(|_| WsError::NonMinimalLength)?;
            let Some(payload) = buffer.get(header..header + length) else {
                return Ok(None);
            };
            let payload = payload.to_vec();
            self.start += header + length;
            match opcode {
                PING => {
                    self.send_frame(PONG, &payload)?;
                    return Ok(Some(WsEvent::Ping));
                }
                PONG => return Ok(Some(WsEvent::Pong)),
                CLOSE => return self.closed_by_server(&payload).map(Some),
                _ => {
                    let (text, mut bytes) =
                        self.partial.take().unwrap_or((opcode == TEXT, Vec::new()));
                    bytes.extend_from_slice(&payload);
                    if !fin {
                        self.partial = Some((text, bytes));
                        continue;
                    }
                    return if text {
                        String::from_utf8(bytes)
                            .map(|text| Some(WsEvent::Text(text)))
                            .map_err(|_| WsError::InvalidUtf8)
                    } else {
                        Ok(Some(WsEvent::Binary(bytes)))
                    };
                }
            }
        }
    }

    /// A close frame arrived: checks it and answers it.
    fn closed_by_server(&mut self, payload: &[u8]) -> Result<WsEvent, WsError> {
        let given = match payload {
            [] => None,
            [_] => return Err(WsError::BadClose),
            [high, low, reason @ ..] => {
                let code = u16::from_be_bytes([*high, *low]);
                if !sendable_close_code(code) {
                    return Err(WsError::BadClose);
                }
                let reason = std::str::from_utf8(reason).map_err(|_| WsError::BadClose)?;
                Some((code, reason.to_owned()))
            }
        };
        self.close_received = true;
        if !self.close_sent {
            // The answer echoes the code (RFC 6455 section 5.5.1). The
            // connection ends either way, so a failed answer changes nothing.
            let echo = given
                .as_ref()
                .map(|(code, _)| code.to_be_bytes().to_vec())
                .unwrap_or_default();
            let _ = self.send_frame(CLOSE, &echo);
            self.close_sent = true;
        }
        Ok(WsEvent::Close(given))
    }

    /// Sends one text message as one frame.
    ///
    /// # Errors
    /// Longer than the limit, already closed, or the stream failed.
    pub fn send_text(&mut self, text: &str) -> Result<(), WsError> {
        if text.len() > self.limits.max_text_out {
            return Err(WsError::OutgoingTooBig {
                len: text.len(),
                limit: self.limits.max_text_out,
            });
        }
        self.send_frame(TEXT, text.as_bytes())
    }

    /// Sends one binary message as one frame.
    ///
    /// # Errors
    /// Longer than the limit, already closed, or the stream failed.
    pub fn send_binary(&mut self, data: &[u8]) -> Result<(), WsError> {
        if data.len() > self.limits.max_binary_out {
            return Err(WsError::OutgoingTooBig {
                len: data.len(),
                limit: self.limits.max_binary_out,
            });
        }
        self.send_frame(BINARY, data)
    }

    /// Sends a close frame with `code` and `reason` (cut to fit 123 bytes);
    /// nothing is sent after it. A second call does nothing.
    ///
    /// # Errors
    /// The stream failed.
    pub fn close(&mut self, code: u16, reason: &str) -> Result<(), WsError> {
        if self.close_sent {
            return Ok(());
        }
        let mut cut = reason.len().min(123);
        while !reason.is_char_boundary(cut) {
            cut -= 1;
        }
        let mut payload = code.to_be_bytes().to_vec();
        payload.extend_from_slice(&reason.as_bytes()[..cut]);
        let sent = self.send_frame(CLOSE, &payload);
        self.close_sent = true;
        sent
    }

    /// Writes one final, masked frame.
    fn send_frame(&mut self, opcode: u8, payload: &[u8]) -> Result<(), WsError> {
        if self.close_sent {
            return Err(WsError::AlreadyClosed);
        }
        let mut frame = Vec::with_capacity(payload.len() + 14);
        frame.push(0x80 | opcode);
        match payload.len() {
            n if n < 126 => frame.push(0x80 | n as u8),
            n => match u16::try_from(n) {
                Ok(n) => {
                    frame.push(0x80 | 126);
                    frame.extend_from_slice(&n.to_be_bytes());
                }
                Err(_) => {
                    frame.push(0x80 | 127);
                    frame.extend_from_slice(&(n as u64).to_be_bytes());
                }
            },
        }
        let mask = self.masks.next();
        frame.extend_from_slice(&mask);
        frame.extend(payload.iter().zip(mask.iter().cycle()).map(|(b, m)| b ^ m));
        self.stream
            .write_all(&frame)
            .and_then(|()| self.stream.flush())
            .map_err(|e| WsError::Io(io_text(&e)))
    }

    /// The stream.
    pub fn get_mut(&mut self) -> &mut S {
        &mut self.stream
    }

    /// The stream, giving up the client.
    pub fn into_inner(self) -> S {
        self.stream
    }
}

/// Masking keys for client frames: a xorshift generator seeded from the
/// operating system's random source. RFC 6455 asks for unpredictable masks
/// so a browser page cannot choose the bytes on the wire; this client sends
/// no foreign content, so a seeded generator is enough.
#[derive(Debug)]
struct Masks(u64);

impl Masks {
    fn new() -> Self {
        Self(crate::protocol::random_u64() | 1)
    }

    fn next(&mut self) -> [u8; 4] {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        self.0 = x;
        let bytes = x.to_le_bytes();
        [bytes[0], bytes[1], bytes[2], bytes[3]]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;

    /// A scripted server end.
    #[derive(Debug)]
    enum Step {
        Bytes(Vec<u8>),
        WouldBlock,
        TimedOut,
        /// The 101 answer for the key the client wrote, then these bytes.
        Accept(Vec<u8>),
    }

    #[derive(Debug, Default)]
    struct Script {
        steps: VecDeque<Step>,
        written: Vec<u8>,
        /// When the script runs out: `WouldBlock` (true) or the end (false).
        idle_blocks: bool,
    }

    impl Script {
        fn new(steps: impl IntoIterator<Item = Step>) -> Self {
            Self {
                steps: steps.into_iter().collect(),
                written: Vec::new(),
                idle_blocks: true,
            }
        }

        fn ending(steps: impl IntoIterator<Item = Step>) -> Self {
            Self {
                idle_blocks: false,
                ..Self::new(steps)
            }
        }

        fn key(&self) -> String {
            let text = String::from_utf8_lossy(&self.written);
            text.lines()
                .find_map(|line| line.strip_prefix("Sec-WebSocket-Key: "))
                .unwrap_or_default()
                .trim()
                .to_owned()
        }
    }

    impl Read for Script {
        fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
            match self.steps.pop_front() {
                None if self.idle_blocks => Err(io::ErrorKind::WouldBlock.into()),
                None => Ok(0),
                Some(Step::WouldBlock) => Err(io::ErrorKind::WouldBlock.into()),
                Some(Step::TimedOut) => Err(io::ErrorKind::TimedOut.into()),
                Some(Step::Accept(after)) => {
                    let mut answer = format!(
                        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {}\r\n\r\n",
                        accept_key(&self.key())
                    )
                    .into_bytes();
                    answer.extend(after);
                    self.steps.push_front(Step::Bytes(answer));
                    self.read(buf)
                }
                Some(Step::Bytes(mut bytes)) => {
                    let n = bytes.len().min(buf.len());
                    buf[..n].copy_from_slice(&bytes[..n]);
                    if n < bytes.len() {
                        self.steps.push_front(Step::Bytes(bytes.split_off(n)));
                    }
                    Ok(n)
                }
            }
        }
    }

    impl Write for Script {
        fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
            self.written.extend_from_slice(buf);
            Ok(buf.len())
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    /// A server frame (unmasked).
    fn frame(fin: bool, opcode: u8, payload: &[u8]) -> Vec<u8> {
        let mut out = vec![if fin { 0x80 } else { 0 } | opcode];
        match payload.len() {
            n if n < 126 => out.push(n as u8),
            n if n <= 0xFFFF => {
                out.push(126);
                out.extend((n as u16).to_be_bytes());
            }
            n => {
                out.push(127);
                out.extend((n as u64).to_be_bytes());
            }
        }
        out.extend_from_slice(payload);
        out
    }

    /// The frames a client wrote, unmasked: (fin, opcode, payload). Every
    /// one must be masked.
    fn client_frames(mut bytes: &[u8]) -> Vec<(bool, u8, Vec<u8>)> {
        let mut frames = Vec::new();
        while !bytes.is_empty() {
            let fin = bytes[0] & 0x80 != 0;
            let opcode = bytes[0] & 0x0F;
            assert!(bytes[1] & 0x80 != 0, "a client frame is masked");
            let (len, mut at) = match bytes[1] & 0x7F {
                126 => (usize::from(u16::from_be_bytes([bytes[2], bytes[3]])), 4),
                127 => (
                    usize::try_from(u64::from_be_bytes(bytes[2..10].try_into().unwrap())).unwrap(),
                    10,
                ),
                n => (usize::from(n), 2),
            };
            let mask = [bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]];
            at += 4;
            let payload = bytes[at..at + len]
                .iter()
                .enumerate()
                .map(|(i, b)| b ^ mask[i % 4])
                .collect();
            frames.push((fin, opcode, payload));
            bytes = &bytes[at + len..];
        }
        frames
    }

    fn later() -> Instant {
        Instant::now() + Duration::from_secs(5)
    }

    fn open(steps: impl IntoIterator<Item = Step>) -> WsClient<Script> {
        WsClient::upgraded(Script::new(steps), WsLimits::default(), Vec::new())
    }

    fn token() -> Secret {
        Secret::new("tok-123")
    }

    fn request<'a>(bearer: Option<&'a Secret>) -> Handshake<'a> {
        Handshake {
            host: "relay.example:8797",
            path: "/client/ws",
            bearer,
        }
    }

    fn read_all(client: &mut WsClient<Script>) -> Result<Vec<WsEvent>, WsError> {
        let mut events = Vec::new();
        while let Some(event) = client.read_event()? {
            events.push(event);
        }
        Ok(events)
    }

    // RFC 6455 section 1.3's example.
    #[test]
    fn accept_key_matches_the_rfc_example() {
        assert_eq!(
            accept_key("dGhlIHNhbXBsZSBub25jZQ=="),
            "s3pPLMBiTxaQ9kYGzzhZRbK+xOo="
        );
    }

    #[test]
    fn handshake_sends_the_upgrade_request_with_the_token() {
        let secret = token();
        let client = WsClient::handshake(
            Script::new([Step::Accept(Vec::new())]),
            &request(Some(&secret)),
            WsLimits::default(),
            later(),
        )
        .unwrap();
        let script = client.into_inner();
        let text = String::from_utf8(script.written.clone()).unwrap();
        let mut lines = text.split("\r\n");
        assert_eq!(lines.next(), Some("GET /client/ws HTTP/1.1"));
        let headers: Vec<&str> = lines.collect();
        for wanted in [
            "Host: relay.example:8797",
            "Upgrade: websocket",
            "Connection: Upgrade",
            "Sec-WebSocket-Version: 13",
            "Authorization: Bearer tok-123",
        ] {
            assert!(headers.contains(&wanted), "{wanted} in {text}");
        }
        assert!(text.ends_with("\r\n\r\n"));
        // 16 random bytes in base64.
        let key = script.key();
        assert_eq!(key.len(), 24);
        assert!(key.ends_with("=="));
    }

    #[test]
    fn handshake_without_a_token_sends_no_authorization() {
        let client = WsClient::handshake(
            Script::new([Step::Accept(Vec::new())]),
            &request(None),
            WsLimits::default(),
            later(),
        )
        .unwrap();
        let text = String::from_utf8(client.into_inner().written).unwrap();
        assert!(!text.contains("Authorization"), "{text}");
    }

    #[test]
    fn keys_differ_between_handshakes() {
        let key = || {
            WsClient::handshake(
                Script::new([Step::Accept(Vec::new())]),
                &request(None),
                WsLimits::default(),
                later(),
            )
            .unwrap()
            .into_inner()
            .key()
        };
        assert_ne!(key(), key());
    }

    #[test]
    fn bytes_after_the_answer_are_the_first_frames() {
        let mut client = WsClient::handshake(
            Script::new([Step::Accept(frame(true, 1, b"hi"))]),
            &request(None),
            WsLimits::default(),
            later(),
        )
        .unwrap();
        assert_eq!(client.read_event(), Ok(Some(WsEvent::Text("hi".into()))));
        assert_eq!(client.read_event(), Ok(None));
    }

    #[test]
    fn handshake_waits_through_timeouts_and_split_answers() {
        // Reads that time out, then the answer split in two.
        let client = WsClient::handshake(
            SplitAnswer(Script::new([Step::WouldBlock, Step::TimedOut])),
            &request(None),
            WsLimits::default(),
            later(),
        );
        assert!(client.is_ok());
    }

    /// Answers 101 in two pieces, with a timeout between, once the request
    /// was written and the scripted steps ran out.
    #[derive(Debug)]
    struct SplitAnswer(Script);

    impl Read for SplitAnswer {
        fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
            if self.0.steps.is_empty() && !self.0.written.is_empty() {
                let answer = format!(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {}\r\n\r\n",
                    accept_key(&self.0.key())
                )
                .into_bytes();
                let (head, tail) = answer.split_at(20);
                self.0.steps.push_back(Step::Bytes(head.to_vec()));
                self.0.steps.push_back(Step::TimedOut);
                self.0.steps.push_back(Step::Bytes(tail.to_vec()));
                self.0.written.clear();
            }
            self.0.read(buf)
        }
    }

    impl Write for SplitAnswer {
        fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
            self.0.write(buf)
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    fn answer(text: &str) -> Result<WsClient<Script>, HandshakeError> {
        WsClient::handshake(
            Script::ending([Step::Bytes(text.as_bytes().to_vec())]),
            &request(Some(&token())),
            WsLimits::default(),
            later(),
        )
    }

    #[test]
    fn a_refusal_is_its_status_code() {
        let error = answer("HTTP/1.1 401 Unauthorized\r\nContent-Length: 13\r\n\r\nBearer tok-123")
            .unwrap_err();
        assert_eq!(error, HandshakeError::Status(401));
        let shown = format!("{error} {error:?}");
        assert!(!shown.contains("tok-123"), "{shown}");
        assert_eq!(
            answer("HTTP/1.1 503 Busy\r\n\r\n").unwrap_err(),
            HandshakeError::Status(503)
        );
    }

    #[test]
    fn a_bad_answer_is_a_typed_error() {
        assert_eq!(answer("").unwrap_err(), HandshakeError::Closed);
        assert_eq!(
            answer("HTTP/1.1 101 Switching").unwrap_err(),
            HandshakeError::Closed
        );
        assert_eq!(
            answer("SSH-2.0-x\r\n\r\n").unwrap_err(),
            HandshakeError::Malformed
        );
        assert_eq!(
            answer("HTTP/1.1 1x1 What\r\n\r\n").unwrap_err(),
            HandshakeError::Malformed
        );
        assert_eq!(
            answer("HTTP/1.1 101 OK\r\nbroken header\r\n\r\n").unwrap_err(),
            HandshakeError::Malformed
        );
        assert_eq!(
            answer("HTTP/1.1 101 OK\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: x\r\n\r\n")
                .unwrap_err(),
            HandshakeError::NotUpgraded
        );
        assert_eq!(
            answer("HTTP/1.1 101 OK\r\nUpgrade: websocket\r\nConnection: keep-alive\r\n\r\n")
                .unwrap_err(),
            HandshakeError::NotUpgraded
        );
        assert_eq!(
            answer("HTTP/1.1 101 OK\r\nUpgrade: WebSocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Accept: wrong=\r\n\r\n")
                .unwrap_err(),
            HandshakeError::BadAccept
        );
        assert_eq!(
            answer("HTTP/1.1 101 OK\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n")
                .unwrap_err(),
            HandshakeError::BadAccept
        );
        let huge = format!(
            "HTTP/1.1 101 OK\r\nX: {}\r\n\r\n",
            "a".repeat(MAX_RESPONSE_HEADER)
        );
        assert_eq!(answer(&huge).unwrap_err(), HandshakeError::HeaderTooLarge);
    }

    #[test]
    fn an_extension_nobody_asked_for_is_refused() {
        for header in [
            "Sec-WebSocket-Extensions: permessage-deflate",
            "Sec-WebSocket-Protocol: chat",
        ] {
            let client = WsClient::handshake(
                ExtensionServer {
                    inner: Script::new([]),
                    header,
                },
                &request(None),
                WsLimits::default(),
                later(),
            );
            assert_eq!(client.unwrap_err(), HandshakeError::Extension);
        }
    }

    /// Answers 101 with the right accept and one extra header.
    #[derive(Debug)]
    struct ExtensionServer {
        inner: Script,
        header: &'static str,
    }

    impl Read for ExtensionServer {
        fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
            if self.inner.steps.is_empty() {
                let answer = format!(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {}\r\n{}\r\n\r\n",
                    accept_key(&self.inner.key()),
                    self.header
                );
                self.inner.steps.push_back(Step::Bytes(answer.into_bytes()));
            }
            self.inner.read(buf)
        }
    }

    impl Write for ExtensionServer {
        fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
            self.inner.write(buf)
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    #[test]
    fn a_silent_server_times_out() {
        let started = Instant::now();
        let error = WsClient::handshake(
            Script::new([]),
            &request(None),
            WsLimits::default(),
            Instant::now() + Duration::from_millis(30),
        )
        .unwrap_err();
        assert_eq!(error, HandshakeError::Timeout);
        assert!(started.elapsed() < Duration::from_secs(2));
    }

    #[test]
    fn header_injection_is_refused() {
        for (host, path, bearer) in [
            ("a\r\nX: y", "/", None),
            ("a", "/x\r\nX: y", None),
            ("a", "/", Some(Secret::new("t\r\nX: y"))),
            ("a", "/", Some(Secret::new("t u"))),
        ] {
            let error = WsClient::handshake(
                Script::new([Step::Accept(Vec::new())]),
                &Handshake {
                    host,
                    path,
                    bearer: bearer.as_ref(),
                },
                WsLimits::default(),
                later(),
            )
            .unwrap_err();
            assert_eq!(error, HandshakeError::BadRequest);
        }
    }

    #[test]
    fn reads_text_and_binary_of_every_length_encoding() {
        let medium = vec![7u8; 300];
        let long = vec![9u8; 70_000];
        let limits = WsLimits {
            max_binary_in: 100_000,
            ..WsLimits::default()
        };
        let mut stream = frame(true, 1, "h\u{e5}j".as_bytes());
        stream.extend(frame(true, 2, &medium));
        stream.extend(frame(true, 2, &long));
        stream.extend(frame(true, 2, b""));
        let mut client = WsClient::upgraded(Script::new([Step::Bytes(stream)]), limits, Vec::new());
        assert_eq!(
            read_all(&mut client),
            Ok(vec![
                WsEvent::Text("h\u{e5}j".into()),
                WsEvent::Binary(medium),
                WsEvent::Binary(long),
                WsEvent::Binary(Vec::new()),
            ])
        );
    }

    #[test]
    fn bytes_arriving_one_at_a_time_with_timeouts_lose_nothing() {
        let mut stream = frame(true, 1, b"first");
        stream.extend(frame(false, 1, b"sec"));
        stream.extend(frame(true, 9, b"p"));
        stream.extend(frame(true, 0, b"ond"));
        let mut steps = Vec::new();
        for (i, byte) in stream.into_iter().enumerate() {
            steps.push(Step::Bytes(vec![byte]));
            steps.push(if i % 2 == 0 {
                Step::WouldBlock
            } else {
                Step::TimedOut
            });
        }
        let mut client = open(steps);
        let mut events = Vec::new();
        for _ in 0..200 {
            if let Some(event) = client.read_event().unwrap() {
                events.push(event);
            }
        }
        assert_eq!(
            events,
            vec![
                WsEvent::Text("first".into()),
                WsEvent::Ping,
                WsEvent::Text("second".into())
            ]
        );
    }

    #[test]
    fn a_ping_is_answered_with_its_payload() {
        let mut client = open([Step::Bytes(frame(true, 9, b"abc"))]);
        assert_eq!(client.read_event(), Ok(Some(WsEvent::Ping)));
        assert_eq!(
            client_frames(&client.get_mut().written),
            vec![(true, 10, b"abc".to_vec())]
        );
        let mut client = open([Step::Bytes(frame(true, 10, b"x"))]);
        assert_eq!(client.read_event(), Ok(Some(WsEvent::Pong)));
        assert!(client.get_mut().written.is_empty());
    }

    #[test]
    fn fragments_are_joined_around_control_frames() {
        let mut stream = frame(false, 1, b"{\"type\":\"det");
        stream.extend(frame(true, 9, b""));
        stream.extend(frame(false, 0, b"ach\","));
        stream.extend(frame(true, 10, b""));
        stream.extend(frame(true, 0, b"\"request_id\":7}"));
        // A character split across two fragments is still one character.
        let name = "\u{e5}".as_bytes();
        stream.extend(frame(false, 1, &name[..1]));
        stream.extend(frame(true, 0, &name[1..]));
        stream.extend(frame(false, 2, b"\x01"));
        stream.extend(frame(true, 0, b"\x02"));
        let mut client = open([Step::Bytes(stream)]);
        assert_eq!(
            read_all(&mut client),
            Ok(vec![
                WsEvent::Ping,
                WsEvent::Pong,
                WsEvent::Text("{\"type\":\"detach\",\"request_id\":7}".into()),
                WsEvent::Text("\u{e5}".into()),
                WsEvent::Binary(vec![1, 2]),
            ])
        );
    }

    #[test]
    fn a_close_is_answered_and_ends_the_connection() {
        let mut payload = 1001u16.to_be_bytes().to_vec();
        payload.extend(b"bye");
        let mut client = open([Step::Bytes(frame(true, 8, &payload))]);
        assert_eq!(
            client.read_event(),
            Ok(Some(WsEvent::Close(Some((1001, "bye".into())))))
        );
        assert_eq!(
            client_frames(&client.get_mut().written),
            vec![(true, 8, 1001u16.to_be_bytes().to_vec())]
        );
        assert_eq!(client.read_event(), Err(WsError::AlreadyClosed));
        assert_eq!(client.send_text("x"), Err(WsError::AlreadyClosed));
        let mut client = open([Step::Bytes(frame(true, 8, b""))]);
        assert_eq!(client.read_event(), Ok(Some(WsEvent::Close(None))));
        assert_eq!(
            client_frames(&client.get_mut().written),
            vec![(true, 8, Vec::new())]
        );
    }

    fn first_error(stream: Vec<u8>) -> WsError {
        let mut client = open([Step::Bytes(stream)]);
        read_all(&mut client).unwrap_err()
    }

    #[test]
    fn invalid_frames_are_typed_errors() {
        assert_eq!(first_error(vec![0xC1, 0x00]), WsError::ReservedBits);
        assert_eq!(first_error(vec![0x91, 0x00]), WsError::ReservedBits);
        assert_eq!(
            first_error(vec![0x81, 0x81, 1, 2, 3, 4, 5]),
            WsError::MaskedFrame
        );
        assert_eq!(first_error(vec![0x83, 0x00]), WsError::UnknownOpcode(3));
        assert_eq!(first_error(vec![0x8B, 0x00]), WsError::UnknownOpcode(11));
        assert_eq!(
            first_error(frame(false, 9, b"")),
            WsError::FragmentedControl
        );
        assert_eq!(
            first_error(frame(true, 9, &[0; 126])),
            WsError::ControlTooLong
        );
        assert_eq!(
            first_error(vec![0x82, 126, 0, 5, 1, 2, 3, 4, 5]),
            WsError::NonMinimalLength
        );
        let mut long = vec![0x82, 127];
        long.extend(200u64.to_be_bytes());
        assert_eq!(first_error(long), WsError::NonMinimalLength);
        let mut huge = vec![0x82, 127];
        huge.extend((1u64 << 63).to_be_bytes());
        assert_eq!(first_error(huge), WsError::NonMinimalLength);
        assert_eq!(
            first_error(frame(true, 0, b"x")),
            WsError::UnexpectedContinuation
        );
        let mut interleaved = frame(false, 1, b"a");
        interleaved.extend(frame(true, 2, b"b"));
        assert_eq!(first_error(interleaved), WsError::InterleavedMessage);
        assert_eq!(
            first_error(frame(true, 1, b"\xff\xfe")),
            WsError::InvalidUtf8
        );
        let mut split_bad = frame(false, 1, b"\xc3");
        split_bad.extend(frame(true, 0, b"x"));
        assert_eq!(first_error(split_bad), WsError::InvalidUtf8);
    }

    #[test]
    fn invalid_close_frames_are_typed_errors() {
        assert_eq!(first_error(frame(true, 8, &[3])), WsError::BadClose);
        for code in [0u16, 999, 1004, 1005, 1006, 1015, 1016, 2999, 5000] {
            assert_eq!(
                first_error(frame(true, 8, &code.to_be_bytes())),
                WsError::BadClose,
                "{code}"
            );
        }
        let mut bad_reason = 1000u16.to_be_bytes().to_vec();
        bad_reason.push(0xff);
        assert_eq!(first_error(frame(true, 8, &bad_reason)), WsError::BadClose);
        for code in [1000u16, 1003, 1007, 1011, 3000, 4999] {
            let mut client = open([Step::Bytes(frame(true, 8, &code.to_be_bytes()))]);
            assert!(
                matches!(client.read_event(), Ok(Some(WsEvent::Close(Some((c, _))))) if c == code)
            );
        }
    }

    #[test]
    fn an_oversized_message_fails_before_its_payload_arrives() {
        let limits = WsLimits {
            max_text_in: 10,
            max_binary_in: 20,
            ..WsLimits::default()
        };
        // Only the header: the declared length alone is enough.
        let header = frame(true, 1, &[b'a'; 11])[..2].to_vec();
        let mut client = WsClient::upgraded(Script::new([Step::Bytes(header)]), limits, Vec::new());
        assert_eq!(read_all(&mut client), Err(WsError::TooBig { limit: 10 }));
        let header = frame(true, 2, &[0; 21])[..2].to_vec();
        let mut client = WsClient::upgraded(Script::new([Step::Bytes(header)]), limits, Vec::new());
        assert_eq!(read_all(&mut client), Err(WsError::TooBig { limit: 20 }));
        // Fragments add up.
        let mut fragments = frame(false, 1, &[b'a'; 6]);
        fragments.extend(frame(true, 0, &[b'a'; 5]));
        let mut client =
            WsClient::upgraded(Script::new([Step::Bytes(fragments)]), limits, Vec::new());
        assert_eq!(read_all(&mut client), Err(WsError::TooBig { limit: 10 }));
        // At the limit is fine.
        let mut client = WsClient::upgraded(
            Script::new([Step::Bytes(frame(true, 1, &[b'a'; 10]))]),
            limits,
            Vec::new(),
        );
        assert_eq!(
            read_all(&mut client),
            Ok(vec![WsEvent::Text("a".repeat(10))])
        );
    }

    #[test]
    fn the_end_of_the_stream_is_an_error() {
        let mut client = WsClient::upgraded(
            Script::ending([Step::Bytes(frame(true, 1, b"abc")[..3].to_vec())]),
            WsLimits::default(),
            Vec::new(),
        );
        assert_eq!(read_all(&mut client), Err(WsError::Closed));
    }

    #[test]
    fn sent_frames_are_masked_and_sized() {
        let mut client = open([]);
        client.send_text("hello").unwrap();
        client.send_binary(&[1; 125]).unwrap();
        client.send_binary(&[2; 126]).unwrap();
        client.send_binary(&[3; 65_536]).unwrap();
        client.close(1001, "going away").unwrap();
        client.close(1000, "again").unwrap();
        let written = client.get_mut().written.clone();
        assert_eq!(written[0], 0x81);
        assert_eq!(written[1], 0x80 | 5);
        assert_eq!(
            client_frames(&written),
            vec![
                (true, 1, b"hello".to_vec()),
                (true, 2, vec![1; 125]),
                (true, 2, vec![2; 126]),
                (true, 2, vec![3; 65_536]),
                (true, 8, {
                    let mut p = 1001u16.to_be_bytes().to_vec();
                    p.extend(b"going away");
                    p
                }),
            ]
        );
        assert_eq!(client.send_binary(b"x"), Err(WsError::AlreadyClosed));
    }

    #[test]
    fn masks_change_between_frames() {
        let mut client = open([]);
        client.send_binary(&[0; 4]).unwrap();
        client.send_binary(&[0; 4]).unwrap();
        let written = &client.get_mut().written;
        // With a zero payload the masked bytes are the mask itself.
        assert_ne!(written[2..6], written[12..16]);
    }

    #[test]
    fn messages_over_the_send_limits_are_refused() {
        let limits = WsLimits {
            max_text_out: 4,
            max_binary_out: 8,
            ..WsLimits::default()
        };
        let mut client = WsClient::upgraded(Script::new([]), limits, Vec::new());
        assert_eq!(
            client.send_text("12345"),
            Err(WsError::OutgoingTooBig { len: 5, limit: 4 })
        );
        assert_eq!(
            client.send_binary(&[0; 9]),
            Err(WsError::OutgoingTooBig { len: 9, limit: 8 })
        );
        assert!(client.get_mut().written.is_empty());
        client.send_binary(&[0; 8]).unwrap();
    }

    #[test]
    fn a_long_close_reason_is_cut_at_a_character() {
        let mut client = open([]);
        client.close(1000, &"\u{e5}".repeat(100)).unwrap();
        let frames = client_frames(&client.get_mut().written);
        let payload = &frames[0].2;
        assert!(payload.len() <= 125);
        assert!(std::str::from_utf8(&payload[2..]).is_ok());
    }
}
