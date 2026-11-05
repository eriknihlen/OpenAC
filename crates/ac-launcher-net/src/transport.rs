//! The launcher's HTTP transport: one `GET` per connection, over the
//! operating system's TLS for HTTPS and over plain TCP only to this machine.
//!
//! The socket is non-blocking underneath a small adapter that presents it
//! as an ordinary blocking stream to the TLS library and the HTTP parser:
//! when no bytes are ready it pauses briefly and looks at the caller's
//! cancellation and the request's deadline before trying again. So a
//! request waiting on a silent server stops as soon as it is cancelled or
//! out of time, on every platform (a socket shut down from another thread
//! does not wake a blocked read on Windows), and a read is never retried
//! after a socket-level timeout (which Windows documents as leaving a TCP
//! socket in an indeterminate state).

use std::io::{self, BufReader, Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};
use std::time::{Duration, Instant};

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::net::{HttpError, HttpRequest, HttpResponse, HttpTransport, USER_AGENT};
use ac_launcher_core::url::WebUrl;

use ac_proxy::{
    Dialer, Proxies, ProxyKind, ProxyServer, ProxyStream, SystemProxy, Target, TunnelError,
    open_tunnel,
};

use crate::bounded::{self, Interrupted};
use crate::http1::{self, BodyReader, Framing, HeadError};

/// Bytes buffered between the connection and the response parser.
const READ_BUFFER: usize = 64 * 1024;
/// The first pause when the socket has nothing ready; it doubles while
/// nothing comes, up to [`LONGEST_PAUSE`], and starts over when bytes move.
const FIRST_PAUSE: Duration = Duration::from_millis(1);
/// The longest pause between looks at an idle socket.
const LONGEST_PAUSE: Duration = Duration::from_millis(10);

/// An HTTP/1.1 client over the operating system's TLS stack (the launcher
/// core's [`HttpTransport`]). Certificates are checked against the system's
/// trust store and the URL's host name; TLS 1.2 is the oldest version
/// accepted.
#[derive(Clone)]
pub struct OsHttpTransport {
    connector: native_tls::TlsConnector,
    user_agent: String,
    proxy: SystemProxy,
}

impl std::fmt::Debug for OsHttpTransport {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OsHttpTransport")
            .field("user_agent", &self.user_agent)
            .field("proxy", &self.proxy)
            .finish_non_exhaustive()
    }
}

impl OsHttpTransport {
    /// A transport sending the launcher's user agent ([`USER_AGENT`]).
    /// Fails only when the system's TLS library cannot be set up.
    pub fn new() -> Result<Self, HttpError> {
        let connector = native_tls::TlsConnector::builder()
            .min_protocol_version(Some(native_tls::Protocol::Tlsv12))
            .build()
            .map_err(|error| {
                HttpError::Failed(format!("The system TLS library is unavailable: {error}"))
            })?;
        Ok(Self {
            connector,
            user_agent: USER_AGENT.to_owned(),
            proxy: SystemProxy::detect(),
        })
    }

    /// The same transport going through `proxy` (tests).
    #[cfg(test)]
    fn with_proxy(mut self, proxy: SystemProxy) -> Self {
        self.proxy = proxy;
        self
    }

    fn fetch(
        &self,
        request: &HttpRequest,
        cancel: &CancelToken,
    ) -> Result<HttpResponse, HttpError> {
        let url = &request.url;
        let deadline = request.timeout.map(|timeout| Instant::now() + timeout);
        let tls = if url.is_https() {
            true
        } else if url.is_http() && url.is_loopback() {
            false
        } else {
            return Err(HttpError::Failed(format!(
                "The launcher fetches only HTTPS addresses (plain HTTP only from this machine): {url}"
            )));
        };
        if !url.user_info().is_empty() {
            return Err(HttpError::Failed(
                "The launcher does not send credentials in an address.".to_owned(),
            ));
        }
        let request_bytes = http1::request(url, &self.user_agent).map_err(HttpError::Failed)?;
        let host = url.host().trim_start_matches('[').trim_end_matches(']');
        let port = url.port().ok_or_else(|| {
            HttpError::Failed(format!("The address names no port and no default: {url}"))
        })?;

        let interruption = Interruption::default();
        let proxies = if tls {
            self.route(url, deadline, cancel)?
        } else {
            Proxies::direct()
        };
        let mut connection = proxies
            .try_each(
                |proxy| match proxy {
                    Some(proxy) => {
                        let tunnel =
                            self.tunnel(proxy, url.host(), port, deadline, cancel, &interruption)?;
                        match self.connector.connect(host, tunnel) {
                            Ok(stream) => Ok(Connection::Tls(Box::new(stream))),
                            Err(error) => Err(Attempt::failed(interruption.explain(format!(
                                "The secure connection to {host} through the proxy {proxy} failed: {error}"
                            )))),
                        }
                    }
                    None => self
                        .direct(host, port, tls, deadline, cancel, &interruption)
                        .map_err(Attempt::failed),
                },
                |attempt| attempt.unreachable,
            )
            .map_err(|attempt| attempt.error)?;
        if let Err(error) = connection
            .write_all(&request_bytes)
            .and_then(|()| connection.flush())
        {
            return Err(
                interruption.explain(format!("Sending the request to {host} failed: {error}"))
            );
        }
        let mut reader = BufReader::with_capacity(READ_BUFFER, connection);
        let head = match http1::read_head(&mut reader) {
            Ok(head) => head,
            Err(HeadError::Io(error)) => {
                return Err(interruption
                    .explain(format!("Reading the response from {host} failed: {error}")));
            }
            Err(HeadError::Invalid(text)) => {
                return Err(HttpError::Failed(format!("{text} ({url})")));
            }
        };
        let framing = head.framing().map_err(HttpError::Failed)?;
        let content_length = match framing {
            Framing::Length(length) => Some(length),
            Framing::Empty => head.content_length().map_err(HttpError::Failed)?,
            Framing::Chunked | Framing::UntilClose => None,
        };
        Ok(HttpResponse {
            status: head.status,
            location: head.location(),
            content_length,
            content_encoding: head.content_encoding(),
            requested_url: url.clone(),
            body: Box::new(ResponseBody {
                reader: BodyReader::new(reader, framing),
                interruption,
            }),
        })
    }
}

impl HttpTransport for OsHttpTransport {
    fn get(&self, request: &HttpRequest, cancel: &CancelToken) -> Result<HttpResponse, HttpError> {
        if cancel.is_cancelled() {
            return Err(HttpError::Cancelled);
        }
        self.fetch(request, cancel)
    }
}

/// A connection attempt that failed; `unreachable` when the proxy itself
/// could not be connected to, so the next proxy of a list is tried (.NET's
/// `RequestRetryType.RetryOnNextProxy`).
struct Attempt {
    error: HttpError,
    unreachable: bool,
}

impl Attempt {
    fn failed(error: HttpError) -> Self {
        Self {
            error,
            unreachable: false,
        }
    }
}

impl OsHttpTransport {
    /// A connection straight to `host:port` (TLS to it with `tls`; plain
    /// only to this machine).
    fn direct(
        &self,
        host: &str,
        port: u16,
        tls: bool,
        deadline: Option<Instant>,
        cancel: &CancelToken,
        interruption: &Interruption,
    ) -> Result<Connection, HttpError> {
        let socket = connect(host, port, deadline, cancel)?;
        if !tls && !socket.peer_addr().is_ok_and(|peer| peer.ip().is_loopback()) {
            return Err(HttpError::Failed(format!(
                "Plain HTTP is allowed only to this machine, but '{host}' is not a loopback address."
            )));
        }
        let socket = polling(socket, host, deadline, cancel, interruption)?;
        if !tls {
            return Ok(Connection::Plain(socket));
        }
        match self.connector.connect(host, Tunnel::Plain(socket)) {
            Ok(stream) => Ok(Connection::Tls(Box::new(stream))),
            Err(error) => {
                Err(interruption
                    .explain(format!("The secure connection to {host} failed: {error}")))
            }
        }
    }

    /// The proxies for `url`. On Windows the user's automatic configuration
    /// may fetch and run a script, so this runs where the request's
    /// deadline and cancellation are watched.
    fn route(
        &self,
        url: &WebUrl,
        deadline: Option<Instant>,
        cancel: &CancelToken,
    ) -> Result<Proxies, HttpError> {
        let proxy = self.proxy.clone();
        let host = url.host().to_owned();
        let address = url.to_string();
        let route = move || proxy.route(&Target::new("https", &host, &address));
        match bounded::run("http-proxy", route, deadline, cancel) {
            Ok(Ok(route)) => Ok(route),
            Ok(Err(text)) => Err(HttpError::Failed(text)),
            Err(Some(Interrupted::Cancelled)) => Err(HttpError::Cancelled),
            Err(Some(Interrupted::TimedOut)) => Err(HttpError::TimedOut),
            Err(None) => Err(HttpError::Failed(
                "Finding the system proxy failed: its thread stopped.".to_owned(),
            )),
        }
    }

    /// A byte stream to `host:port` through `proxy` (the shared tunnel:
    /// `CONNECT` with the proxy's authentication, or SOCKS), within the
    /// request's deadline and cancellation.
    fn tunnel(
        &self,
        proxy: &ProxyServer,
        host: &str,
        port: u16,
        deadline: Option<Instant>,
        cancel: &CancelToken,
        interruption: &Interruption,
    ) -> Result<Tunnel, Attempt> {
        let mut dialer = ProxyDialer {
            connector: &self.connector,
            proxy,
            deadline,
            cancel,
            interruption,
            unreachable: false,
        };
        open_tunnel(&mut dialer, proxy, host, port, Some(&self.user_agent)).map_err(|error| {
            let error = match error {
                TunnelError::Dial(error) => error,
                TunnelError::Io(error) => {
                    interruption.explain(format!("Talking to the proxy {proxy} failed: {error}"))
                }
                TunnelError::Refused(text) => HttpError::Failed(text),
            };
            Attempt {
                error,
                unreachable: dialer.unreachable,
            }
        })
    }
}

/// Reaches a proxy for [`open_tunnel`]: a new polled connection (over TLS
/// to an `https` proxy), and the server's IPv4 address for SOCKS 4.
struct ProxyDialer<'a> {
    connector: &'a native_tls::TlsConnector,
    proxy: &'a ProxyServer,
    deadline: Option<Instant>,
    cancel: &'a CancelToken,
    interruption: &'a Interruption,
    /// The proxy could not be connected to (not timed out or cancelled).
    unreachable: bool,
}

impl Dialer for ProxyDialer<'_> {
    type Stream = Tunnel;
    type Error = HttpError;

    fn dial(&mut self) -> Result<Tunnel, HttpError> {
        let host = self.proxy.bare_host();
        let socket = connect(host, self.proxy.port, self.deadline, self.cancel)
            .inspect_err(|error| self.unreachable = matches!(error, HttpError::Failed(_)))?;
        let socket = polling(socket, host, self.deadline, self.cancel, self.interruption)?;
        if self.proxy.kind == ProxyKind::Https {
            match self.connector.connect(host, socket) {
                Ok(stream) => Ok(Tunnel::Tls(Box::new(stream))),
                Err(error) => Err(self.interruption.explain(format!(
                    "The secure connection failed to the proxy {}: {error}",
                    self.proxy
                ))),
            }
        } else {
            Ok(Tunnel::Plain(socket))
        }
    }

    fn resolve_v4(&mut self, host: &str, port: u16) -> Result<std::net::Ipv4Addr, HttpError> {
        resolve_v4(host, port, self.deadline, self.cancel)
    }
}

/// The server's first IPv4 address, for SOCKS 4.
fn resolve_v4(
    host: &str,
    port: u16,
    deadline: Option<Instant>,
    cancel: &CancelToken,
) -> Result<std::net::Ipv4Addr, HttpError> {
    let target = host.to_owned();
    let job = move || {
        (target.as_str(), port).to_socket_addrs().map(|addresses| {
            addresses
                .into_iter()
                .find_map(|address| match address.ip() {
                    std::net::IpAddr::V4(v4) => Some(v4),
                    std::net::IpAddr::V6(_) => None,
                })
        })
    };
    match bounded::run("http-resolve", job, deadline, cancel) {
        Ok(Ok(Some(address))) => Ok(address),
        Ok(Ok(None)) => Err(HttpError::Failed(format!(
            "{host} has no IPv4 address, which a SOCKS 4 proxy needs."
        ))),
        Ok(Err(error)) => Err(HttpError::Failed(format!(
            "Could not resolve {host}: {error}"
        ))),
        Err(Some(Interrupted::Cancelled)) => Err(HttpError::Cancelled),
        Err(Some(Interrupted::TimedOut)) => Err(HttpError::TimedOut),
        Err(None) => Err(HttpError::Failed(format!(
            "Could not resolve {host}: the resolver thread stopped."
        ))),
    }
}

/// `socket` made non-blocking under a [`PollingSocket`].
fn polling(
    socket: TcpStream,
    host: &str,
    deadline: Option<Instant>,
    cancel: &CancelToken,
    interruption: &Interruption,
) -> Result<PollingSocket, HttpError> {
    socket.set_nonblocking(true).map_err(|error| {
        HttpError::Failed(format!(
            "Could not prepare the connection to {host}: {error}"
        ))
    })?;
    Ok(PollingSocket {
        socket,
        deadline,
        cancel: cancel.clone(),
        interruption: interruption.clone(),
    })
}

/// Resolves `host` and connects to the first address that answers, on a
/// helper thread so the caller's cancellation and deadline are honoured
/// even while the system resolver or a connect is stuck.
fn connect(
    host: &str,
    port: u16,
    deadline: Option<Instant>,
    cancel: &CancelToken,
) -> Result<TcpStream, HttpError> {
    let target = host.to_owned();
    let job = move || -> io::Result<TcpStream> {
        let mut last = None;
        for address in (target.as_str(), port).to_socket_addrs()? {
            let attempt = match deadline {
                None => TcpStream::connect(address),
                Some(deadline) => match deadline.checked_duration_since(Instant::now()) {
                    Some(left) if !left.is_zero() => TcpStream::connect_timeout(&address, left),
                    _ => Err(io::Error::from(io::ErrorKind::TimedOut)),
                },
            };
            match attempt {
                Ok(stream) => return Ok(stream),
                Err(error) => last = Some(error),
            }
        }
        Err(last.unwrap_or_else(|| {
            io::Error::new(io::ErrorKind::NotFound, "the name has no addresses")
        }))
    };
    match bounded::run("http-connect", job, deadline, cancel) {
        Ok(Ok(stream)) => {
            // The request is small; send it at once.
            let _ = stream.set_nodelay(true);
            Ok(stream)
        }
        Ok(Err(error)) if error.kind() == io::ErrorKind::TimedOut && deadline.is_some() => {
            Err(HttpError::TimedOut)
        }
        Ok(Err(error)) => Err(HttpError::Failed(format!(
            "Could not connect to {host}:{port}: {error}"
        ))),
        Err(Some(Interrupted::Cancelled)) => Err(HttpError::Cancelled),
        Err(Some(Interrupted::TimedOut)) => Err(HttpError::TimedOut),
        Err(None) => Err(HttpError::Failed(format!(
            "Could not connect to {host}:{port}: the connect thread stopped."
        ))),
    }
}

// 0 (the default) is "not interrupted".
const CANCELLED: u8 = 1;
const TIMED_OUT: u8 = 2;

/// Whether a request's I/O was stopped by its caller or its deadline, kept
/// beside the connection so that whatever error the TLS library or the
/// parser then reports is told as what happened.
#[derive(Debug, Clone, Default)]
struct Interruption(Arc<AtomicU8>);

impl Interruption {
    fn record(&self, reason: Interrupted) {
        let value = match reason {
            Interrupted::Cancelled => CANCELLED,
            Interrupted::TimedOut => TIMED_OUT,
        };
        self.0.store(value, Ordering::SeqCst);
    }

    fn get(&self) -> Option<Interrupted> {
        match self.0.load(Ordering::SeqCst) {
            CANCELLED => Some(Interrupted::Cancelled),
            TIMED_OUT => Some(Interrupted::TimedOut),
            _ => None,
        }
    }

    /// The error for a failure: the cancellation or timeout behind it, else
    /// `message`.
    fn explain(&self, message: String) -> HttpError {
        match self.get() {
            Some(Interrupted::Cancelled) => HttpError::Cancelled,
            Some(Interrupted::TimedOut) => HttpError::TimedOut,
            None => HttpError::Failed(message),
        }
    }
}

/// An `io::Error` that carries the [`HttpError`] it stands for: `TimedOut`
/// (kind `TimedOut`) or `Cancelled` (kind `Other`).
fn interrupted_error(reason: Interrupted) -> io::Error {
    match reason {
        Interrupted::Cancelled => io::Error::other(HttpError::Cancelled),
        Interrupted::TimedOut => io::Error::new(io::ErrorKind::TimedOut, HttpError::TimedOut),
    }
}

/// A non-blocking socket read and written as if blocking, stopping with an
/// error when the caller cancels or the deadline passes.
#[derive(Debug)]
struct PollingSocket {
    socket: TcpStream,
    deadline: Option<Instant>,
    cancel: CancelToken,
    interruption: Interruption,
}

impl PollingSocket {
    fn wait_for<T>(
        &mut self,
        mut attempt: impl FnMut(&mut TcpStream) -> io::Result<T>,
    ) -> io::Result<T> {
        let mut pause = FIRST_PAUSE;
        loop {
            if let Some(reason) = bounded::interrupted(&self.cancel, self.deadline) {
                self.interruption.record(reason);
                return Err(interrupted_error(reason));
            }
            match attempt(&mut self.socket) {
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    std::thread::sleep(pause);
                    pause = (pause * 2).min(LONGEST_PAUSE);
                }
                Err(error) if error.kind() == io::ErrorKind::Interrupted => {}
                result => return result,
            }
        }
    }
}

impl Read for PollingSocket {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if buffer.is_empty() {
            return Ok(0);
        }
        self.wait_for(|socket| socket.read(buffer))
    }
}

impl Write for PollingSocket {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        if buffer.is_empty() {
            return Ok(0);
        }
        self.wait_for(|socket| socket.write(buffer))
    }

    fn flush(&mut self) -> io::Result<()> {
        self.wait_for(|socket| socket.flush())
    }
}

/// The byte stream TLS to the server runs over: the socket, or a tunnel
/// through a proxy (over TLS to the proxy for an `https` one).
#[derive(Debug)]
enum Tunnel {
    Plain(PollingSocket),
    Tls(Box<native_tls::TlsStream<PollingSocket>>),
}

impl Read for Tunnel {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        match self {
            Self::Plain(stream) => stream.read(buffer),
            Self::Tls(stream) => stream.read(buffer),
        }
    }
}

impl Write for Tunnel {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        match self {
            Self::Plain(stream) => stream.write(buffer),
            Self::Tls(stream) => stream.write(buffer),
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        match self {
            Self::Plain(stream) => stream.flush(),
            Self::Tls(stream) => stream.flush(),
        }
    }
}

impl ProxyStream for Tunnel {
    fn channel_binding(&self) -> Option<Vec<u8>> {
        match self {
            Self::Plain(_) => None,
            Self::Tls(stream) => stream.tls_server_end_point().ok().flatten(),
        }
    }
}

/// The byte stream under the HTTP parser.
enum Connection {
    Plain(PollingSocket),
    Tls(Box<native_tls::TlsStream<Tunnel>>),
}

impl Read for Connection {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        match self {
            Self::Plain(stream) => stream.read(buffer),
            Self::Tls(stream) => stream.read(buffer),
        }
    }
}

impl Write for Connection {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        match self {
            Self::Plain(stream) => stream.write(buffer),
            Self::Tls(stream) => stream.write(buffer),
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        match self {
            Self::Plain(stream) => stream.flush(),
            Self::Tls(stream) => stream.flush(),
        }
    }
}

/// The streamed body handed to the launcher core. A read stopped by the
/// caller or the deadline fails with an `io::Error` carrying
/// `HttpError::Cancelled` or `HttpError::TimedOut`.
struct ResponseBody {
    reader: BodyReader<BufReader<Connection>>,
    interruption: Interruption,
}

impl Read for ResponseBody {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        self.reader
            .read(buffer)
            .map_err(|error| match self.interruption.get() {
                Some(reason) => interrupted_error(reason),
                None => error,
            })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;

    /// A body that stalls part-way is cut off by the request's deadline,
    /// and the error says so. The deadline starts only once the body's
    /// first bytes are already in the socket, so the outcome does not
    /// depend on thread scheduling (a deadline that also had to cover a
    /// server thread's reply ran out before the head on a loaded machine).
    #[test]
    fn a_body_that_stalls_times_out_at_the_request_deadline() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let client = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let (mut server, _) = listener.accept().unwrap();
        server.write_all(b"part").unwrap();
        server.flush().unwrap();
        client.set_nonblocking(true).unwrap();

        let timeout = Duration::from_millis(300);
        let started = Instant::now();
        let interruption = Interruption::default();
        let socket = PollingSocket {
            socket: client,
            deadline: Some(started + timeout),
            cancel: CancelToken::new(),
            interruption: interruption.clone(),
        };
        let mut body = ResponseBody {
            reader: BodyReader::new(
                BufReader::new(Connection::Plain(socket)),
                Framing::Length(100),
            ),
            interruption,
        };
        let mut bytes = Vec::new();
        let error = body.read_to_end(&mut bytes).unwrap_err();
        // The server is still connected and silent: only the deadline can
        // have ended the read.
        assert!(started.elapsed() >= timeout);
        assert_eq!(error.kind(), io::ErrorKind::TimedOut);
        assert_eq!(
            ac_launcher_core::net::body_read_error(error, &CancelToken::new()),
            HttpError::TimedOut
        );
        drop(server);
    }

    fn read_head(stream: &mut TcpStream) -> String {
        let mut head = Vec::new();
        let mut byte = [0u8; 1];
        while !head.ends_with(b"\r\n\r\n") {
            stream.read_exact(&mut byte).unwrap();
            head.push(byte[0]);
        }
        String::from_utf8(head).unwrap()
    }

    fn through(proxy: ProxyServer) -> Result<HttpResponse, HttpError> {
        let transport = OsHttpTransport::new()
            .unwrap()
            .with_proxy(SystemProxy::fixed(proxy));
        transport.get(
            &HttpRequest {
                url: WebUrl::parse("https://example.invalid/feed.json").unwrap(),
                timeout: Some(Duration::from_secs(20)),
            },
            &CancelToken::new(),
        )
    }

    fn local_proxy(kind: ProxyKind, port: u16, credentials: Option<(&str, &str)>) -> ProxyServer {
        ProxyServer {
            kind,
            host: "127.0.0.1".into(),
            port,
            credentials: credentials.map(|(u, p)| ac_proxy::Credentials::Named {
                domain: None,
                user: u.to_owned(),
                password: p.to_owned(),
            }),
        }
    }

    // .NET `HttpClient` through an HTTP proxy: `CONNECT host:port` with the
    // Host and User-Agent headers; a 407 Basic challenge is answered once
    // with the credentials of the proxy's address, on a new connection;
    // then TLS to the server starts inside the tunnel (the proxy sees a TLS
    // handshake record, never the request).
    #[test]
    fn an_http_proxy_tunnel_answers_a_basic_challenge_then_carries_tls() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = std::thread::spawn(move || {
            let (mut first, _) = listener.accept().unwrap();
            let head = read_head(&mut first);
            assert!(
                head.starts_with("CONNECT example.invalid:443 HTTP/1.1\r\n"),
                "{head}"
            );
            assert!(head.contains("\r\nHost: example.invalid:443\r\n"), "{head}");
            assert!(head.contains("\r\nUser-Agent: "), "{head}");
            assert!(!head.contains("Proxy-Authorization"), "{head}");
            first
                .write_all(
                    b"HTTP/1.1 407 Proxy Authentication Required\r\n\
                      Proxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n",
                )
                .unwrap();
            drop(first);
            let (mut second, _) = listener.accept().unwrap();
            let head = read_head(&mut second);
            // "user:pa ss" in Base64.
            assert!(
                head.contains("\r\nProxy-Authorization: Basic dXNlcjpwYSBzcw==\r\n"),
                "{head}"
            );
            second
                .write_all(b"HTTP/1.1 200 Connection established\r\n\r\n")
                .unwrap();
            let mut record = [0u8; 1];
            second.read_exact(&mut record).unwrap();
            record[0]
        });
        let Err(error) = through(local_proxy(ProxyKind::Http, port, Some(("user", "pa ss"))))
        else {
            panic!("the fake proxy closes the tunnel");
        };
        assert_eq!(server.join().unwrap(), 0x16, "a TLS handshake record");
        match error {
            HttpError::Failed(text) => assert!(text.contains("through the proxy"), "{text}"),
            other => panic!("{other:?}"),
        }
    }

    fn get_through(transport: &OsHttpTransport) -> Result<HttpResponse, HttpError> {
        transport.get(
            &HttpRequest {
                url: WebUrl::parse("https://example.invalid/feed.json").unwrap(),
                timeout: Some(Duration::from_secs(20)),
            },
            &CancelToken::new(),
        )
    }

    /// A fake HTTP proxy that refuses one `CONNECT` with a 403.
    fn refusing_proxy(listener: TcpListener) -> std::thread::JoinHandle<String> {
        std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let head = read_head(&mut stream);
            stream
                .write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n")
                .unwrap();
            head
        })
    }

    /// A port nothing listens on: connecting to it is refused.
    fn closed_port() -> u16 {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        listener.local_addr().unwrap().port()
    }

    /// Whether a connection is waiting at `listener`.
    fn was_contacted(listener: &TcpListener) -> bool {
        listener.set_nonblocking(true).unwrap();
        listener.accept().is_ok()
    }

    fn expect_403(outcome: Result<HttpResponse, HttpError>) {
        match outcome.err().unwrap() {
            HttpError::Failed(text) => assert!(text.contains("HTTP 403"), "{text}"),
            other => panic!("{other:?}"),
        }
    }

    // .NET `HttpConnectionPoolManager.SendAsyncMultiProxy` over a Windows
    // list: the first proxy cannot be connected to, so the request goes
    // through the second; the next request skips the first (`MultiProxy`
    // remembers it for 30 minutes) even once it listens again.
    #[test]
    fn a_proxy_list_falls_over_to_the_next_proxy_that_can_be_reached() {
        let first = closed_port();
        let second = TcpListener::bind("127.0.0.1:0").unwrap();
        let second_port = second.local_addr().unwrap().port();
        let transport = OsHttpTransport::new()
            .unwrap()
            .with_proxy(SystemProxy::listed(&format!(
                "127.0.0.1:{first};127.0.0.1:{second_port}"
            )));
        let server = std::thread::spawn(move || {
            let mut heads = Vec::new();
            for _ in 0..2 {
                let (mut stream, _) = second.accept().unwrap();
                heads.push(read_head(&mut stream));
                stream
                    .write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n")
                    .unwrap();
            }
            heads
        });
        expect_403(get_through(&transport));
        let first_again = TcpListener::bind(("127.0.0.1", first)).unwrap();
        expect_403(get_through(&transport));
        let heads = server.join().unwrap();
        assert!(
            heads
                .iter()
                .all(|head| head.starts_with("CONNECT example.invalid:443 HTTP/1.1\r\n")),
            "{heads:?}"
        );
        assert!(!was_contacted(&first_again), "the failed proxy is skipped");
    }

    // .NET retries on the next proxy only when one cannot be connected to:
    // a proxy that answers but refuses the tunnel ends the request.
    #[test]
    fn a_proxy_that_refuses_the_tunnel_is_not_passed_over() {
        let first = TcpListener::bind("127.0.0.1:0").unwrap();
        let first_port = first.local_addr().unwrap().port();
        let second = TcpListener::bind("127.0.0.1:0").unwrap();
        let second_port = second.local_addr().unwrap().port();
        let server = refusing_proxy(first);
        let transport = OsHttpTransport::new()
            .unwrap()
            .with_proxy(SystemProxy::listed(&format!(
                "127.0.0.1:{first_port} 127.0.0.1:{second_port}"
            )));
        expect_403(get_through(&transport));
        server.join().unwrap();
        assert!(!was_contacted(&second));
    }

    // .NET 10 reads no `socks=` entry from the Windows settings: the
    // request goes through the list's HTTP proxy, never the SOCKS one.
    #[test]
    fn a_socks_entry_of_a_windows_list_is_not_used() {
        let socks = TcpListener::bind("127.0.0.1:0").unwrap();
        let socks_port = socks.local_addr().unwrap().port();
        let http = TcpListener::bind("127.0.0.1:0").unwrap();
        let http_port = http.local_addr().unwrap().port();
        let server = refusing_proxy(http);
        let transport = OsHttpTransport::new()
            .unwrap()
            .with_proxy(SystemProxy::listed(&format!(
                "socks=127.0.0.1:{socks_port};127.0.0.1:{http_port}"
            )));
        expect_403(get_through(&transport));
        assert!(server.join().unwrap().starts_with("CONNECT "));
        assert!(!was_contacted(&socks));
    }

    #[test]
    fn a_proxy_that_refuses_the_tunnel_fails_the_request_saying_so() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            read_head(&mut stream);
            stream
                .write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n")
                .unwrap();
        });
        let error = through(local_proxy(ProxyKind::Http, port, None))
            .err()
            .unwrap();
        server.join().unwrap();
        match error {
            HttpError::Failed(text) => assert!(text.contains("HTTP 403"), "{text}"),
            other => panic!("{other:?}"),
        }
    }

    // RFC 1928 and RFC 1929: the greeting offers user name and password
    // because the address carries them; the server is named, for the proxy
    // to resolve; then TLS starts in the tunnel.
    #[test]
    fn a_socks5_proxy_logs_in_names_the_server_and_carries_tls() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = std::thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            let mut greeting = [0u8; 4];
            s.read_exact(&mut greeting).unwrap();
            assert_eq!(greeting, [5, 2, 0, 2]);
            s.write_all(&[5, 2]).unwrap();
            let mut login = [0u8; 9];
            s.read_exact(&mut login).unwrap();
            assert_eq!(&login, b"\x01\x02me\x04word");
            s.write_all(&[1, 0]).unwrap();
            let mut request = vec![0u8; 5 + 15 + 2];
            s.read_exact(&mut request).unwrap();
            let mut expected = vec![5, 1, 0, 3, 15];
            expected.extend_from_slice(b"example.invalid");
            expected.extend_from_slice(&443u16.to_be_bytes());
            assert_eq!(request, expected);
            s.write_all(&[5, 0, 0, 1, 127, 0, 0, 1, 0, 80]).unwrap();
            let mut record = [0u8; 1];
            s.read_exact(&mut record).unwrap();
            record[0]
        });
        let error = through(local_proxy(ProxyKind::Socks5, port, Some(("me", "word"))));
        assert!(error.is_err());
        assert_eq!(server.join().unwrap(), 0x16);
    }

    // SOCKS 4a: the null address 0.0.0.1, the user id, then the name.
    #[test]
    fn a_socks4a_proxy_is_given_the_servers_name() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = std::thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            let mut request = vec![0u8; 8 + 3 + 16];
            s.read_exact(&mut request).unwrap();
            let mut expected = vec![4, 1, 1, 187, 0, 0, 0, 1];
            expected.extend_from_slice(b"me\0example.invalid\0");
            assert_eq!(request, expected);
            s.write_all(&[0, 0x5B, 0, 0, 0, 0, 0, 0]).unwrap();
        });
        let error = through(local_proxy(ProxyKind::Socks4a, port, Some(("me", ""))))
            .err()
            .unwrap();
        server.join().unwrap();
        match error {
            HttpError::Failed(text) => assert!(text.contains("SOCKS 4 reply 91"), "{text}"),
            other => panic!("{other:?}"),
        }
    }
}
