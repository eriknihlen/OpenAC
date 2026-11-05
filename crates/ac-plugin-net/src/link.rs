//! A connection a worker thread drives: a non-blocking TCP socket under a
//! small adapter, optionally wrapped in TLS. While connecting and for HTTP
//! the adapter waits (briefly sleeping) for bytes until the deadline, so
//! the TLS library and the HTTP reader see an ordinary blocking stream; a
//! socket in use by a plugin switches reads to non-blocking so the worker
//! can write between them. Writes always wait until the bytes are out.

use std::io::{self, Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::time::{Duration, Instant};

/// The longest pause between looks at an idle socket.
const LONGEST_PAUSE: Duration = Duration::from_millis(10);

/// The socket under TLS.
#[derive(Debug)]
pub(crate) struct Polling {
    socket: TcpStream,
    deadline: Option<Instant>,
    /// Reads wait for bytes (connecting, HTTP) rather than answering
    /// `WouldBlock`.
    pub(crate) blocking_reads: bool,
    /// A write gives up past this (a closing socket's flush).
    write_deadline: Option<Instant>,
}

fn timed_out() -> io::Error {
    io::Error::from(io::ErrorKind::TimedOut)
}

impl Polling {
    /// An accepted connection (already non-blocking), with no deadline.
    pub(crate) fn from_accepted(socket: TcpStream) -> Self {
        Self {
            socket,
            deadline: None,
            blocking_reads: false,
            write_deadline: None,
        }
    }

    fn wait(&self, pause: &mut Duration) -> io::Result<()> {
        if self.deadline.is_some_and(|d| Instant::now() >= d) {
            return Err(timed_out());
        }
        std::thread::sleep(*pause);
        *pause = (*pause * 2).min(LONGEST_PAUSE);
        Ok(())
    }
}

impl Read for Polling {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        let mut pause = Duration::from_millis(1);
        loop {
            match self.socket.read(buf) {
                Err(e) if e.kind() == io::ErrorKind::WouldBlock && self.blocking_reads => {
                    self.wait(&mut pause)?;
                }
                Err(e) if e.kind() == io::ErrorKind::Interrupted => {}
                other => return other,
            }
        }
    }
}

impl Write for Polling {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        let mut pause = Duration::from_millis(1);
        loop {
            match self.socket.write(buf) {
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => {
                    // A write waits until the bytes are out (a socket's own
                    // deadline does not stop a write in progress; a closing
                    // socket's flush deadline does).
                    if self.write_deadline.is_some_and(|d| Instant::now() >= d) {
                        return Err(timed_out());
                    }
                    std::thread::sleep(pause);
                    pause = (pause * 2).min(LONGEST_PAUSE);
                }
                Err(e) if e.kind() == io::ErrorKind::Interrupted => {}
                other => return other,
            }
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        self.socket.flush()
    }
}

/// A connection: plain, or TLS over another (the socket, or a tunnel
/// through a proxy that is itself spoken to over TLS).
pub(crate) enum Link {
    Plain(Polling),
    Tls(Box<native_tls::TlsStream<Link>>),
}

impl std::fmt::Debug for Link {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::Plain(_) => "Link::Plain",
            Self::Tls(_) => "Link::Tls",
        })
    }
}

impl Link {
    fn polling(&mut self) -> &mut Polling {
        match self {
            Self::Plain(p) => p,
            Self::Tls(t) => t.get_mut().polling(),
        }
    }

    /// Writes give up past `deadline` (none: they wait until the bytes are
    /// out).
    pub(crate) fn set_write_deadline(&mut self, deadline: Option<Instant>) {
        self.polling().write_deadline = deadline;
    }

    /// Ends the sending half (TLS says goodbye first); the other end reads
    /// what was sent, then the end. Failures are moot: the socket is done.
    pub(crate) fn shutdown(&mut self) {
        match self {
            Self::Plain(p) => {
                let _ = p.socket.shutdown(std::net::Shutdown::Write);
            }
            Self::Tls(t) => {
                let _ = t.shutdown();
            }
        }
    }

    /// Reads stop waiting: a read with nothing there answers
    /// `WouldBlock`, and the deadline no longer applies.
    pub(crate) fn stop_waiting(&mut self) {
        let polling = self.polling();
        polling.blocking_reads = false;
        polling.deadline = None;
    }
}

impl Read for Link {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        match self {
            Self::Plain(p) => p.read(buf),
            Self::Tls(t) => t.read(buf),
        }
    }
}

impl Write for Link {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        match self {
            Self::Plain(p) => p.write(buf),
            Self::Tls(t) => t.write(buf),
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        match self {
            Self::Plain(p) => p.flush(),
            Self::Tls(t) => t.flush(),
        }
    }
}

/// Why a connection could not be made.
#[derive(Debug)]
pub(crate) enum ConnectError {
    Timeout,
    Connect(String),
    Tls(String),
}

impl ac_proxy::ProxyStream for Link {
    fn channel_binding(&self) -> Option<Vec<u8>> {
        match self {
            Self::Plain(_) => None,
            Self::Tls(t) => t.tls_server_end_point().ok().flatten(),
        }
    }
}

/// Resolves and connects within `deadline`, then (with `tls`) completes the
/// TLS handshake for `host` (its certificate is checked against it).
pub(crate) fn connect(
    host: &str,
    port: u16,
    tls: bool,
    deadline: Instant,
) -> Result<Link, ConnectError> {
    let link = connect_tcp(host, port, deadline)?;
    if tls { secure(link, host) } else { Ok(link) }
}

/// Completes a TLS handshake for `host` over `link`.
pub(crate) fn secure(link: Link, host: &str) -> Result<Link, ConnectError> {
    let connector = native_tls::TlsConnector::builder()
        .min_protocol_version(Some(native_tls::Protocol::Tlsv12))
        .build()
        .map_err(|e| ConnectError::Tls(format!("the system TLS library is unavailable: {e}")))?;
    match connector.connect(host, link) {
        Ok(stream) => Ok(Link::Tls(Box::new(stream))),
        Err(native_tls::HandshakeError::Failure(e)) => Err(ConnectError::Tls(format!(
            "the secure connection to {host} failed: {e}"
        ))),
        Err(native_tls::HandshakeError::WouldBlock(_)) => Err(ConnectError::Timeout),
    }
}

/// Resolves and connects within `deadline`: the socket, its reads waiting
/// for bytes until the deadline.
pub(crate) fn connect_tcp(host: &str, port: u16, deadline: Instant) -> Result<Link, ConnectError> {
    let mut last = None;
    let mut socket = None;
    let addresses = (host, port)
        .to_socket_addrs()
        .map_err(|e| ConnectError::Connect(format!("{host}: {e}")))?;
    for address in addresses {
        let Some(left) = deadline
            .checked_duration_since(Instant::now())
            .filter(|d| !d.is_zero())
        else {
            return Err(ConnectError::Timeout);
        };
        match TcpStream::connect_timeout(&address, left) {
            Ok(s) => {
                socket = Some(s);
                break;
            }
            Err(e) if e.kind() == io::ErrorKind::TimedOut => return Err(ConnectError::Timeout),
            Err(e) => last = Some(e),
        }
    }
    let socket = socket.ok_or_else(|| {
        ConnectError::Connect(match last {
            Some(e) => format!("{host}:{port}: {e}"),
            None => format!("{host} has no addresses"),
        })
    })?;
    let _ = socket.set_nodelay(true);
    socket
        .set_nonblocking(true)
        .map_err(|e| ConnectError::Connect(e.to_string()))?;
    Ok(Link::Plain(Polling {
        socket,
        deadline: Some(deadline),
        blocking_reads: true,
        write_deadline: None,
    }))
}
