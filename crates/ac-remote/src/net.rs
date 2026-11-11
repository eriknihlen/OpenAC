//! The socket under the WebSocket: a non-blocking TCP socket under a small
//! adapter, optionally wrapped in TLS (the same pattern as the plugins'
//! sockets). While connecting the adapter waits, briefly sleeping, for bytes
//! until a deadline, so the TLS library and the handshake see an ordinary
//! blocking stream; once connected, reads answer `WouldBlock` at once so the
//! worker can write between them. Every wait also ends when the link is
//! told to stop, so stopping never waits on the network.

use std::io::{self, Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver};
use std::time::{Duration, Instant};

/// The longest pause between looks at an idle socket.
const LONGEST_PAUSE: Duration = Duration::from_millis(10);

/// The socket, under TLS or not.
#[derive(Debug)]
pub(crate) struct Polling {
    socket: TcpStream,
    /// Reads wait for bytes (connecting) rather than answering `WouldBlock`.
    blocking_reads: bool,
    /// A waiting read gives up past this.
    read_deadline: Option<Instant>,
    /// A write gives up past this.
    write_deadline: Option<Instant>,
    stop: Arc<AtomicBool>,
}

fn stopped() -> io::Error {
    io::Error::other("the link is stopping")
}

impl Polling {
    /// `socket` (made non-blocking), its reads waiting until `deadline`.
    pub(crate) fn new(
        socket: TcpStream,
        deadline: Instant,
        stop: Arc<AtomicBool>,
    ) -> io::Result<Self> {
        socket.set_nonblocking(true)?;
        let _ = socket.set_nodelay(true);
        Ok(Self {
            socket,
            blocking_reads: true,
            read_deadline: Some(deadline),
            write_deadline: Some(deadline),
            stop,
        })
    }

    fn pause(&self, deadline: Option<Instant>, pause: &mut Duration) -> io::Result<()> {
        if self.stop.load(Ordering::SeqCst) {
            return Err(stopped());
        }
        if deadline.is_some_and(|d| Instant::now() >= d) {
            return Err(io::ErrorKind::TimedOut.into());
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
                    self.pause(self.read_deadline, &mut pause)?;
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
                    self.pause(self.write_deadline, &mut pause)?;
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

/// The connection: plain (local tests) or TLS.
pub(crate) enum Transport {
    Plain(Polling),
    Tls(Box<native_tls::TlsStream<Polling>>),
}

impl std::fmt::Debug for Transport {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::Plain(_) => "Transport::Plain",
            Self::Tls(_) => "Transport::Tls",
        })
    }
}

impl Transport {
    fn polling(&mut self) -> &mut Polling {
        match self {
            Self::Plain(p) => p,
            Self::Tls(t) => t.get_mut(),
        }
    }

    /// Connected: reads answer `WouldBlock` at once.
    pub(crate) fn stop_waiting(&mut self) {
        let polling = self.polling();
        polling.blocking_reads = false;
        polling.read_deadline = None;
    }

    /// Writes give up past `deadline`.
    pub(crate) fn set_write_deadline(&mut self, deadline: Instant) {
        self.polling().write_deadline = Some(deadline);
    }

    /// Ends the connection (TLS says goodbye first). Failures are moot: the
    /// socket is done.
    pub(crate) fn shutdown(&mut self) {
        if let Self::Tls(t) = self {
            let _ = t.shutdown();
        }
        let _ = self.polling().socket.shutdown(std::net::Shutdown::Both);
    }
}

impl Read for Transport {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        match self {
            Self::Plain(p) => p.read(buf),
            Self::Tls(t) => t.read(buf),
        }
    }
}

impl Write for Transport {
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

/// Resolves `host` and connects within `timeout` on a helper thread, so a
/// slow name lookup or connect never holds up a stop: the answer arrives on
/// the channel (a helper that outlives its caller just drops its socket).
pub(crate) fn dial(
    host: &str,
    port: u16,
    timeout: Duration,
) -> io::Result<Receiver<Result<TcpStream, String>>> {
    let (answer, answered) = mpsc::channel();
    let host = host.to_owned();
    std::thread::Builder::new()
        .name("remote-dial".to_owned())
        .spawn(move || {
            let _ = answer.send(connect_tcp(&host, port, Instant::now() + timeout));
        })?;
    Ok(answered)
}

fn connect_tcp(host: &str, port: u16, deadline: Instant) -> Result<TcpStream, String> {
    let addresses = (host, port)
        .to_socket_addrs()
        .map_err(|e| format!("{host}: {e}"))?;
    let mut last = None;
    for address in addresses {
        let Some(left) = deadline
            .checked_duration_since(Instant::now())
            .filter(|d| !d.is_zero())
        else {
            return Err(format!("{host}:{port}: timed out"));
        };
        match TcpStream::connect_timeout(&address, left) {
            Ok(socket) => return Ok(socket),
            Err(e) => last = Some(e),
        }
    }
    Err(match last {
        Some(e) => format!("{host}:{port}: {e}"),
        None => format!("{host} has no addresses"),
    })
}

/// Completes a TLS handshake for `host` (its certificate is checked against
/// it) over `polling`.
pub(crate) fn secure(polling: Polling, host: &str) -> Result<Transport, String> {
    let connector = native_tls::TlsConnector::builder()
        .min_protocol_version(Some(native_tls::Protocol::Tlsv12))
        .build()
        .map_err(|e| format!("the system TLS library is unavailable: {e}"))?;
    match connector.connect(host, polling) {
        Ok(stream) => Ok(Transport::Tls(Box::new(stream))),
        Err(native_tls::HandshakeError::Failure(e)) => {
            Err(format!("the secure connection to {host} failed: {e}"))
        }
        Err(native_tls::HandshakeError::WouldBlock(_)) => {
            Err(format!("the secure connection to {host} timed out"))
        }
    }
}
