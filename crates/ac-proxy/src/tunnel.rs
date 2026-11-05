//! A byte stream to a server through a proxy, as .NET's handler opens one
//! for HTTPS, a WebSocket, or any request through a SOCKS proxy:
//! `CONNECT host:port` through an `http` or `https` proxy (answering its
//! `407` by [`ProxyAuth`], on the same connection while the proxy keeps it
//! open), or the SOCKS 4, 4a or 5 handshake. What runs inside the tunnel
//! (TLS to the server, or plain bytes) is the caller's.

use std::fmt;
use std::io::{self, Read, Write};
use std::net::Ipv4Addr;

use crate::auth::{ProxyAuth, Step};
use crate::resolve::{Credentials, ProxyKind, ProxyServer};

/// The most a proxy's answer head may take.
const MAX_HEAD_BYTES: usize = 64 * 1024;
/// The most header lines it may carry.
const MAX_HEADERS: usize = 256;
/// The most of a `407` body read to keep its connection (.NET drains up
/// to 1 MiB of a response before reusing a connection).
const MAX_DRAIN_BYTES: u64 = 1024 * 1024;

/// A connection to a proxy.
pub trait ProxyStream: Read + Write {
    /// The RFC 5929 `tls-server-end-point` hash of the proxy's certificate
    /// when the proxy is spoken to over TLS (the channel binding NTLM and
    /// Negotiate carry); `None` over plain TCP.
    fn channel_binding(&self) -> Option<Vec<u8>> {
        None
    }
}

/// How the caller reaches the proxy, within its own deadline and
/// cancellation.
pub trait Dialer {
    /// A connection to the proxy.
    type Stream: ProxyStream;
    /// The caller's own failure (connecting, timing out, cancelled).
    type Error;

    /// A new connection to the proxy (over TLS to an `https` one).
    ///
    /// # Errors
    /// The caller's: the proxy could not be reached.
    fn dial(&mut self) -> Result<Self::Stream, Self::Error>;

    /// The server's first IPv4 address, for a SOCKS 4 proxy.
    ///
    /// # Errors
    /// The caller's: the name could not be resolved.
    fn resolve_v4(&mut self, host: &str, port: u16) -> Result<Ipv4Addr, Self::Error>;
}

/// Why a tunnel could not be opened.
#[derive(Debug)]
pub enum TunnelError<E> {
    /// The dialer's failure.
    Dial(E),
    /// Reading from or writing to the proxy failed.
    Io(io::Error),
    /// The proxy refused, or answered in a way that cannot be followed.
    Refused(String),
}

impl<E: fmt::Display> fmt::Display for TunnelError<E> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Dial(error) => error.fmt(f),
            Self::Io(error) => error.fmt(f),
            Self::Refused(text) => f.write_str(text),
        }
    }
}

impl<E> From<io::Error> for TunnelError<E> {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

/// A tunnel to `host:port` (an IPv6 address with or without brackets)
/// through `proxy`. `user_agent` is the request's, which .NET copies into
/// the `CONNECT`.
///
/// # Errors
/// The proxy cannot be reached, refuses, or answers in a way that cannot be
/// followed.
pub fn open_tunnel<D: Dialer>(
    dialer: &mut D,
    proxy: &ProxyServer,
    host: &str,
    port: u16,
    user_agent: Option<&str>,
) -> Result<D::Stream, TunnelError<D::Error>> {
    let bare = host.trim_start_matches('[').trim_end_matches(']');
    match proxy.kind {
        ProxyKind::Http | ProxyKind::Https => connect(dialer, proxy, bare, port, user_agent),
        ProxyKind::Socks5 => {
            let mut stream = dialer.dial().map_err(TunnelError::Dial)?;
            socks5(&mut stream, bare, port, socks_login(proxy).as_ref())?;
            Ok(stream)
        }
        ProxyKind::Socks4 | ProxyKind::Socks4a => {
            let address = if proxy.kind == ProxyKind::Socks4 {
                Some(dialer.resolve_v4(bare, port).map_err(TunnelError::Dial)?)
            } else {
                None
            };
            let mut stream = dialer.dial().map_err(TunnelError::Dial)?;
            let user = socks_login(proxy).map(|(user, _)| user).unwrap_or_default();
            socks4(&mut stream, bare, port, address, &user)?;
            Ok(stream)
        }
    }
}

/// The user name and password a SOCKS proxy gets (.NET `SocksHelper`
/// takes the credential's user name and password; the domain is not sent).
fn socks_login(proxy: &ProxyServer) -> Option<(String, String)> {
    proxy.credentials.as_ref().map(|credentials: &Credentials| {
        (
            credentials.user().to_owned(),
            credentials.password().to_owned(),
        )
    })
}

/// A proxy's answer head.
#[derive(Debug)]
struct Head {
    minor_version: u8,
    status: u16,
    headers: Vec<(String, String)>,
}

impl Head {
    fn values<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a str> + 'a {
        self.headers
            .iter()
            .filter(move |(n, _)| n.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }

    fn has_token(&self, name: &str, token: &str) -> bool {
        self.values(name)
            .flat_map(|v| v.split(','))
            .any(|t| t.trim().eq_ignore_ascii_case(token))
    }

    /// Whether the proxy closes the connection after this answer.
    fn closes(&self) -> bool {
        self.has_token("connection", "close")
            || (self.minor_version == 0 && !self.has_token("connection", "keep-alive"))
    }
}

/// Reads one line (up to LF, CR stripped) byte by byte, so nothing past the
/// head is taken from the stream (the tunnel's bytes follow it).
fn read_line(
    stream: &mut impl Read,
    budget: &mut usize,
    any_read: &mut bool,
) -> io::Result<String> {
    let mut line = Vec::new();
    let mut byte = [0u8; 1];
    loop {
        if *budget == 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "the proxy's answer head is too large",
            ));
        }
        if stream.read(&mut byte)? == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "the proxy closed the connection",
            ));
        }
        *any_read = true;
        *budget -= 1;
        if byte[0] == b'\n' {
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            return String::from_utf8(line)
                .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "a header is not text"));
        }
        line.push(byte[0]);
    }
}

/// The answer's head (interim `1xx` answers skipped). `any_read` tells
/// whether the proxy sent anything at all.
fn read_head(stream: &mut impl Read, any_read: &mut bool) -> io::Result<Head> {
    let invalid = |text: &str| io::Error::new(io::ErrorKind::InvalidData, text.to_owned());
    let mut budget = MAX_HEAD_BYTES;
    loop {
        let status_line = read_line(stream, &mut budget, any_read)?;
        let mut parts = status_line.splitn(3, ' ');
        let minor_version = match parts.next() {
            Some("HTTP/1.1") => 1,
            Some("HTTP/1.0") => 0,
            _ => return Err(invalid("the proxy did not answer in HTTP/1.x")),
        };
        let status: u16 = parts
            .next()
            .and_then(|s| s.parse().ok())
            .filter(|s| (100..=999).contains(s))
            .ok_or_else(|| invalid("the proxy's status code is not a number"))?;
        let mut headers = Vec::new();
        loop {
            let line = read_line(stream, &mut budget, any_read)?;
            if line.is_empty() {
                break;
            }
            if headers.len() == MAX_HEADERS {
                return Err(invalid("the proxy's answer has too many headers"));
            }
            let (name, value) = line
                .split_once(':')
                .ok_or_else(|| invalid("a header of the proxy's answer has no colon"))?;
            headers.push((name.trim().to_owned(), value.trim().to_owned()));
        }
        if (100..200).contains(&status) {
            continue;
        }
        return Ok(Head {
            minor_version,
            status,
            headers,
        });
    }
}

/// Reads and drops a `407`'s body so its connection can carry the next
/// request: `false` when the connection cannot (a body until close, or one
/// too large to drain).
fn drain(stream: &mut impl Read, head: &Head) -> io::Result<bool> {
    let chunked = head
        .values("transfer-encoding")
        .any(|v| v.to_ascii_lowercase().contains("chunked"));
    if chunked {
        let mut drained = 0u64;
        let mut any = false;
        loop {
            let mut budget = 4096;
            let line = read_line(stream, &mut budget, &mut any)?;
            let size = u64::from_str_radix(line.split(';').next().unwrap_or("").trim(), 16)
                .map_err(|_| {
                    io::Error::new(io::ErrorKind::InvalidData, "a chunk size is not a number")
                })?;
            if size == 0 {
                let mut trailer_budget = 16 * 1024;
                while !read_line(stream, &mut trailer_budget, &mut any)?.is_empty() {}
                return Ok(true);
            }
            drained += size;
            if drained > MAX_DRAIN_BYTES {
                return Ok(false);
            }
            io::copy(&mut stream.by_ref().take(size), &mut io::sink())?;
            let mut crlf_budget = 2;
            read_line(stream, &mut crlf_budget, &mut any)?;
        }
    }
    let Some(length) = head.values("content-length").next() else {
        // No length: the body runs until the proxy closes.
        return Ok(false);
    };
    let length: u64 = length
        .parse()
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "the length is not a number"))?;
    if length > MAX_DRAIN_BYTES {
        return Ok(false);
    }
    let copied = io::copy(&mut stream.by_ref().take(length), &mut io::sink())?;
    if copied < length {
        return Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "the proxy closed the connection inside its answer",
        ));
    }
    Ok(true)
}

/// The `CONNECT` asking an HTTP proxy for a tunnel to `authority`.
fn connect_request(
    authority: &str,
    user_agent: Option<&str>,
    authorization: Option<&str>,
) -> Vec<u8> {
    let mut text = format!("CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n");
    if let Some(agent) = user_agent {
        text.push_str(&format!("User-Agent: {agent}\r\n"));
    }
    if let Some(value) = authorization {
        text.push_str(&format!("Proxy-Authorization: {value}\r\n"));
    }
    text.push_str("\r\n");
    text.into_bytes()
}

/// `CONNECT` through an HTTP proxy, answering its `407`s. A request sent on
/// a connection that already carried one is sent once more on a new
/// connection if the proxy closed it before answering (.NET retries a
/// request that failed on a reused connection).
fn connect<D: Dialer>(
    dialer: &mut D,
    proxy: &ProxyServer,
    host: &str,
    port: u16,
    user_agent: Option<&str>,
) -> Result<D::Stream, TunnelError<D::Error>> {
    let authority = if host.contains(':') {
        format!("[{host}]:{port}")
    } else {
        format!("{host}:{port}")
    };
    if !authority.bytes().all(|byte| byte.is_ascii_graphic())
        || user_agent.is_some_and(|agent| agent.bytes().any(|byte| byte.is_ascii_control()))
    {
        return Err(TunnelError::Refused(
            "The address cannot be sent to the proxy.".to_owned(),
        ));
    }
    let mut auth = ProxyAuth::new(proxy);
    let mut authorization: Option<String> = None;
    let mut stream = dialer.dial().map_err(TunnelError::Dial)?;
    let mut reused = false;
    loop {
        let request = connect_request(&authority, user_agent, authorization.as_deref());
        let mut any_read = false;
        let attempt = stream
            .write_all(&request)
            .and_then(|()| stream.flush())
            .and_then(|()| read_head(&mut stream, &mut any_read));
        let head = match attempt {
            Ok(head) => head,
            Err(_) if reused && !any_read => {
                stream = dialer.dial().map_err(TunnelError::Dial)?;
                reused = false;
                continue;
            }
            Err(error) => return Err(TunnelError::Io(error)),
        };
        match head.status {
            200 => {
                if authorization.is_some() {
                    auth.answered(head.values("proxy-authenticate"))
                        .map_err(TunnelError::Refused)?;
                }
                return Ok(stream);
            }
            407 => {
                let endpoint = stream.channel_binding();
                let keep = !head.closes() && drain(&mut stream, &head)?;
                match auth.challenged(
                    head.values("proxy-authenticate"),
                    "CONNECT",
                    "/",
                    endpoint.as_deref(),
                ) {
                    Step::Send(value) => authorization = Some(value),
                    Step::Stop(text) | Step::Fail(text) => return Err(TunnelError::Refused(text)),
                }
                if keep {
                    reused = true;
                } else {
                    stream = dialer.dial().map_err(TunnelError::Dial)?;
                    reused = false;
                }
            }
            status => {
                return Err(TunnelError::Refused(format!(
                    "The proxy {proxy} refused a tunnel to {authority}: HTTP {status}."
                )));
            }
        }
    }
}

fn refused<E>(text: impl Into<String>) -> TunnelError<E> {
    TunnelError::Refused(text.into())
}

/// RFC 1928 with RFC 1929's user name and password: no authentication
/// offered unless the proxy's address carries credentials; the server named
/// by its address (an IP literal) or its name (resolved by the proxy).
fn socks5<E>(
    stream: &mut (impl Read + Write),
    host: &str,
    port: u16,
    login: Option<&(String, String)>,
) -> Result<(), TunnelError<E>> {
    let methods: &[u8] = if login.is_some() { &[0, 2] } else { &[0] };
    let mut greeting = vec![5, methods.len() as u8];
    greeting.extend_from_slice(methods);
    stream.write_all(&greeting)?;
    stream.flush()?;
    let mut choice = [0u8; 2];
    stream.read_exact(&mut choice)?;
    match (choice, login) {
        ([5, 0], _) => {}
        ([5, 2], Some((user, password))) => {
            if user.len() > 255 || password.len() > 255 {
                return Err(refused("SOCKS 5: the user name or password is too long."));
            }
            let mut message = vec![1, user.len() as u8];
            message.extend_from_slice(user.as_bytes());
            message.push(password.len() as u8);
            message.extend_from_slice(password.as_bytes());
            stream.write_all(&message)?;
            stream.flush()?;
            let mut answer = [0u8; 2];
            stream.read_exact(&mut answer)?;
            if answer[1] != 0 {
                return Err(refused(
                    "SOCKS 5: the proxy refused the user name and password.",
                ));
            }
        }
        ([5, 0xFF], _) => {
            return Err(refused(
                "SOCKS 5: the proxy accepts none of the offered logins.",
            ));
        }
        _ => return Err(refused("SOCKS 5: the proxy did not answer as SOCKS 5.")),
    }
    let mut request = vec![5, 1, 0];
    match host.parse::<std::net::IpAddr>() {
        Ok(std::net::IpAddr::V4(address)) => {
            request.push(1);
            request.extend_from_slice(&address.octets());
        }
        Ok(std::net::IpAddr::V6(address)) => {
            request.push(4);
            request.extend_from_slice(&address.octets());
        }
        Err(_) => {
            if host.len() > 255 {
                return Err(refused("SOCKS 5: the host name is too long."));
            }
            request.push(3);
            request.push(host.len() as u8);
            request.extend_from_slice(host.as_bytes());
        }
    }
    request.extend_from_slice(&port.to_be_bytes());
    stream.write_all(&request)?;
    stream.flush()?;
    let mut reply = [0u8; 4];
    stream.read_exact(&mut reply)?;
    if reply[0] != 5 {
        return Err(refused("SOCKS 5: the proxy did not answer as SOCKS 5."));
    }
    if reply[1] != 0 {
        return Err(refused(format!(
            "SOCKS 5: the proxy could not reach the server (SOCKS 5 reply {}).",
            reply[1]
        )));
    }
    // The address the proxy bound, then its port: read and set aside.
    let length = match reply[3] {
        1 => 4,
        4 => 16,
        3 => {
            let mut length = [0u8; 1];
            stream.read_exact(&mut length)?;
            usize::from(length[0])
        }
        _ => {
            return Err(refused(
                "SOCKS 5: the proxy answered with an unknown address type.",
            ));
        }
    };
    let mut rest = vec![0u8; length + 2];
    stream.read_exact(&mut rest)?;
    Ok(())
}

/// SOCKS 4 (the server's IPv4 `address`) or 4a (`None`: its name, resolved
/// by the proxy), with `user` as the user id.
fn socks4<E>(
    stream: &mut (impl Read + Write),
    host: &str,
    port: u16,
    address: Option<Ipv4Addr>,
    user: &str,
) -> Result<(), TunnelError<E>> {
    let mut request = vec![4, 1];
    request.extend_from_slice(&port.to_be_bytes());
    request.extend_from_slice(&address.map_or([0, 0, 0, 1], |a| a.octets()));
    request.extend_from_slice(user.as_bytes());
    request.push(0);
    if address.is_none() {
        request.extend_from_slice(host.as_bytes());
        request.push(0);
    }
    stream.write_all(&request)?;
    stream.flush()?;
    let mut reply = [0u8; 8];
    stream.read_exact(&mut reply)?;
    if reply[1] != 0x5A {
        return Err(refused(format!(
            "SOCKS 4: the proxy could not reach the server (SOCKS 4 reply {}).",
            reply[1]
        )));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::{TcpListener, TcpStream};
    use std::thread;

    impl ProxyStream for TcpStream {}

    struct Local {
        port: u16,
    }

    impl Dialer for Local {
        type Stream = TcpStream;
        type Error = io::Error;

        fn dial(&mut self) -> io::Result<TcpStream> {
            TcpStream::connect(("127.0.0.1", self.port))
        }

        fn resolve_v4(&mut self, _host: &str, _port: u16) -> io::Result<Ipv4Addr> {
            Ok(Ipv4Addr::new(10, 1, 2, 3))
        }
    }

    fn read_request(stream: &mut TcpStream) -> String {
        let mut any = false;
        let mut text = String::new();
        loop {
            let mut budget = 4096;
            let line = read_line(stream, &mut budget, &mut any).unwrap();
            if line.is_empty() {
                return text;
            }
            text.push_str(&line);
            text.push('\n');
        }
    }

    fn proxy(port: u16, credentials: Option<Credentials>) -> ProxyServer {
        ProxyServer {
            kind: ProxyKind::Http,
            host: "127.0.0.1".into(),
            port,
            credentials,
        }
    }

    fn named(user: &str, password: &str) -> Option<Credentials> {
        Some(Credentials::Named {
            domain: None,
            user: user.into(),
            password: password.into(),
        })
    }

    // RFC 9110 9.3.6 and .NET: `CONNECT host:port` with Host, the request's
    // User-Agent; a 407 whose connection stays open is drained and the
    // Digest answer sent on the same connection; the tunnel carries the
    // bytes that follow the 200 untouched.
    #[test]
    fn a_digest_challenge_is_answered_on_the_same_connection() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            let first = read_request(&mut s);
            assert!(first.starts_with("CONNECT example.invalid:443 HTTP/1.1\nHost: example.invalid:443\nUser-Agent: agent/1\n"), "{first}");
            s.write_all(b"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nProxy-Authenticate: Digest realm=\"p\", nonce=\"abc\", qop=\"auth\"\r\nContent-Length: 5\r\n\r\nnope!").unwrap();
            let second = read_request(&mut s);
            assert!(second.contains("\nProxy-Authorization: Digest username=\"me\", realm=\"p\", nonce=\"abc\", uri=\"/\", response=\""), "{second}");
            s.write_all(b"HTTP/1.1 200 Connection established\r\n\r\ntunnelled")
                .unwrap();
        });
        let mut stream = open_tunnel(
            &mut Local { port },
            &proxy(port, named("me", "pw")),
            "example.invalid",
            443,
            Some("agent/1"),
        )
        .unwrap();
        let mut rest = String::new();
        stream.read_to_string(&mut rest).unwrap();
        assert_eq!(rest, "tunnelled");
        server.join().unwrap();
    }

    // A proxy that closes after its 407 (here without saying so) gets the
    // answer on a new connection; the IPv6 server is written in brackets.
    #[test]
    fn a_closed_connection_is_replaced_for_the_answer() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            let first = read_request(&mut s);
            assert!(
                first.starts_with("CONNECT [2001:db8::1]:443 HTTP/1.1\n"),
                "{first}"
            );
            s.write_all(b"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 0\r\n\r\n").unwrap();
            drop(s);
            let (mut s, _) = listener.accept().unwrap();
            let second = read_request(&mut s);
            assert!(
                second.contains("\nProxy-Authorization: Basic bWU6cHc=\n"),
                "{second}"
            );
            s.write_all(b"HTTP/1.1 200 OK\r\n\r\n").unwrap();
        });
        open_tunnel(
            &mut Local { port },
            &proxy(port, named("me", "pw")),
            "[2001:db8::1]",
            443,
            None,
        )
        .unwrap();
        server.join().unwrap();
    }

    // .NET needs exactly 200 for a tunnel; a second 407 after Basic stands.
    #[test]
    fn a_refusal_names_its_status() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            read_request(&mut s);
            s.write_all(b"HTTP/1.1 204 No Content\r\n\r\n").unwrap();
            let (mut s, _) = listener.accept().unwrap();
            read_request(&mut s);
            s.write_all(
                b"HTTP/1.1 407 No\r\nProxy-Authenticate: Basic\r\nConnection: close\r\n\r\n",
            )
            .unwrap();
            drop(s);
            let (mut s, _) = listener.accept().unwrap();
            read_request(&mut s);
            s.write_all(
                b"HTTP/1.1 407 No\r\nProxy-Authenticate: Basic\r\nConnection: close\r\n\r\n",
            )
            .unwrap();
        });
        let error = open_tunnel(
            &mut Local { port },
            &proxy(port, None),
            "example.invalid",
            443,
            None,
        )
        .err()
        .unwrap();
        assert!(error.to_string().contains("HTTP 204"), "{error}");
        let error = open_tunnel(
            &mut Local { port },
            &proxy(port, named("me", "pw")),
            "example.invalid",
            443,
            None,
        )
        .err()
        .unwrap();
        assert!(
            error.to_string().contains("refused the credentials"),
            "{error}"
        );
        server.join().unwrap();
    }

    /// A fake proxy that checks NTLM or Negotiate with the Windows security
    /// packages, as an IIS or Squid proxy on a Windows domain would.
    #[cfg(windows)]
    #[allow(unsafe_code)]
    mod accepting {
        use std::ptr;

        use windows_sys::Win32::Foundation::{SEC_E_OK, SEC_I_CONTINUE_NEEDED};
        use windows_sys::Win32::Security::Authentication::Identity::{
            ASC_REQ_ALLOCATE_MEMORY, ASC_REQ_CONNECTION, AcceptSecurityContext,
            AcquireCredentialsHandleW, DeleteSecurityContext, FreeContextBuffer,
            FreeCredentialsHandle, SECBUFFER_TOKEN, SECBUFFER_VERSION, SECPKG_CRED_INBOUND,
            SECURITY_NATIVE_DREP, SecBuffer, SecBufferDesc,
        };
        use windows_sys::Win32::Security::Credentials::SecHandle;

        /// What the proxy makes of a client token.
        pub(super) enum Outcome {
            /// Send this token back in a 407.
            Continue(Vec<u8>),
            /// Accepted; the last token (maybe empty) goes with the 200.
            Done(Vec<u8>),
            /// Refused.
            Refused,
        }

        pub(super) struct Server {
            credential: SecHandle,
            context: Option<SecHandle>,
        }

        impl Server {
            pub(super) fn new(package: &str) -> Self {
                let name: Vec<u16> = package.encode_utf16().chain(Some(0)).collect();
                let mut credential = SecHandle::default();
                let mut expiry = 0i64;
                // SAFETY: test fake; the name lives across the call.
                let status = unsafe {
                    AcquireCredentialsHandleW(
                        ptr::null(),
                        name.as_ptr(),
                        SECPKG_CRED_INBOUND,
                        ptr::null(),
                        ptr::null(),
                        None,
                        ptr::null(),
                        &raw mut credential,
                        &raw mut expiry,
                    )
                };
                assert_eq!(status, SEC_E_OK, "server credentials");
                Self {
                    credential,
                    context: None,
                }
            }

            pub(super) fn accept(&mut self, token: &[u8]) -> Outcome {
                let mut token = token.to_vec();
                let mut input = SecBuffer {
                    cbBuffer: u32::try_from(token.len()).unwrap(),
                    BufferType: SECBUFFER_TOKEN,
                    pvBuffer: token.as_mut_ptr().cast(),
                };
                let input_desc = SecBufferDesc {
                    ulVersion: SECBUFFER_VERSION,
                    cBuffers: 1,
                    pBuffers: &raw mut input,
                };
                let mut output = SecBuffer {
                    cbBuffer: 0,
                    BufferType: SECBUFFER_TOKEN,
                    pvBuffer: ptr::null_mut(),
                };
                let mut output_desc = SecBufferDesc {
                    ulVersion: SECBUFFER_VERSION,
                    cBuffers: 1,
                    pBuffers: &raw mut output,
                };
                let mut new_context = self.context.unwrap_or_default();
                let current = self.context.as_ref().map_or(ptr::null(), ptr::from_ref);
                let mut attributes = 0u32;
                let mut expiry = 0i64;
                // SAFETY: test fake; every buffer lives across the call and
                // the output token is freed below.
                let status = unsafe {
                    AcceptSecurityContext(
                        &raw const self.credential,
                        current,
                        &raw const input_desc,
                        ASC_REQ_CONNECTION | ASC_REQ_ALLOCATE_MEMORY,
                        SECURITY_NATIVE_DREP,
                        &raw mut new_context,
                        &raw mut output_desc,
                        &raw mut attributes,
                        &raw mut expiry,
                    )
                };
                let reply = if output.pvBuffer.is_null() {
                    Vec::new()
                } else {
                    // SAFETY: allocated by the package; copied, freed once.
                    unsafe {
                        let bytes = std::slice::from_raw_parts(
                            output.pvBuffer.cast::<u8>(),
                            output.cbBuffer as usize,
                        )
                        .to_vec();
                        FreeContextBuffer(output.pvBuffer);
                        bytes
                    }
                };
                match status {
                    SEC_I_CONTINUE_NEEDED => {
                        self.context = Some(new_context);
                        Outcome::Continue(reply)
                    }
                    SEC_E_OK => {
                        self.context = Some(new_context);
                        Outcome::Done(reply)
                    }
                    _ => Outcome::Refused,
                }
            }
        }

        impl Drop for Server {
            fn drop(&mut self) {
                // SAFETY: test fake; handles made above, freed once.
                unsafe {
                    if let Some(context) = self.context.take() {
                        DeleteSecurityContext(&raw const context);
                    }
                    FreeCredentialsHandle(&raw const self.credential);
                }
            }
        }
    }

    /// The token after `Proxy-Authorization: <scheme> ` in a request.
    #[cfg(windows)]
    fn token_of(request: &str, scheme: &str) -> Vec<u8> {
        let prefix = format!("Proxy-Authorization: {scheme} ");
        let line = request
            .lines()
            .find_map(|line| line.strip_prefix(prefix.as_str()))
            .unwrap_or_else(|| panic!("no {scheme} token in {request}"));
        crate::base64::decode(line).unwrap()
    }

    /// A proxy asking for `scheme` (Basic offered too) and checking each
    /// token with the security packages, on one kept-alive connection: the
    /// tokens it was sent, and whether it let the tunnel through.
    #[cfg(windows)]
    fn handshake(scheme: &'static str, credentials: Option<Credentials>) -> (Vec<Vec<u8>>, bool) {
        use accepting::{Outcome, Server};
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let fake = thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            let first = read_request(&mut s);
            assert!(!first.contains("Proxy-Authorization"), "{first}");
            s.write_all(
                format!(
                    "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: {scheme}\r\n\
                     Proxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 0\r\n\r\n"
                )
                .as_bytes(),
            )
            .unwrap();
            let mut server = Server::new(scheme);
            let mut tokens = Vec::new();
            for _ in 0..4 {
                let request = read_request(&mut s);
                let token = token_of(&request, scheme);
                tokens.push(token.clone());
                match server.accept(&token) {
                    Outcome::Continue(reply) => s
                        .write_all(
                            format!(
                                "HTTP/1.1 407 Proxy Authentication Required\r\n\
                                 Proxy-Authenticate: {scheme} {}\r\nContent-Length: 0\r\n\r\n",
                                crate::base64::encode(&reply)
                            )
                            .as_bytes(),
                        )
                        .unwrap(),
                    Outcome::Done(reply) => {
                        let tail = if reply.is_empty() {
                            String::new()
                        } else {
                            format!(
                                "Proxy-Authenticate: {scheme} {}\r\n",
                                crate::base64::encode(&reply)
                            )
                        };
                        s.write_all(
                            format!("HTTP/1.1 200 Connection established\r\n{tail}\r\n").as_bytes(),
                        )
                        .unwrap();
                        return (tokens, true);
                    }
                    Outcome::Refused => {
                        s.write_all(
                            format!(
                                "HTTP/1.1 407 Proxy Authentication Required\r\n\
                                 Proxy-Authenticate: {scheme}\r\nConnection: close\r\n\r\n"
                            )
                            .as_bytes(),
                        )
                        .unwrap();
                        return (tokens, false);
                    }
                }
            }
            (tokens, false)
        });
        let result = open_tunnel(
            &mut Local { port },
            &proxy(port, credentials),
            "example.invalid",
            443,
            None,
        );
        let (tokens, accepted) = fake.join().unwrap();
        assert_eq!(
            result.is_ok(),
            accepted,
            "{:?}",
            result.err().map(|e| e.to_string())
        );
        (tokens, accepted)
    }

    // .NET with `http://:@proxy` (the logon's credentials): NTLM is chosen
    // over Basic and runs through SSPI leg by leg on one connection; a
    // proxy checking with the security packages lets the tunnel through
    // (MS-NLMP: negotiate, challenge, authenticate).
    #[cfg(windows)]
    #[test]
    fn ntlm_with_the_logon_passes_a_proxy_that_checks_it() {
        let (tokens, accepted) = handshake("NTLM", Some(Credentials::Logon));
        assert!(accepted);
        assert_eq!(tokens.len(), 2);
        assert_eq!(&tokens[0][..12], b"NTLMSSP\0\x01\0\0\0");
        assert_eq!(&tokens[1][..12], b"NTLMSSP\0\x03\0\0\0");
    }

    // Negotiate with the logon: SPNEGO around Kerberos or NTLM, however
    // many legs the packages need, the proxy's last token checked.
    #[cfg(windows)]
    #[test]
    fn negotiate_with_the_logon_passes_a_proxy_that_checks_it() {
        let (tokens, accepted) = handshake("Negotiate", Some(Credentials::Logon));
        assert!(accepted);
        assert!(!tokens.is_empty());
    }

    // Named credentials go into the authenticate message (user and domain
    // in UTF-16); a proxy that cannot verify them keeps answering 407 and
    // the tunnel fails.
    #[cfg(windows)]
    #[test]
    fn ntlm_with_named_credentials_sends_them_and_a_refusal_stands() {
        let credentials = Some(Credentials::Named {
            domain: Some("NOSUCHDOMAIN".into()),
            user: "nosuchuser".into(),
            password: "wrong".into(),
        });
        let (tokens, accepted) = handshake("NTLM", credentials);
        assert!(!accepted);
        let authenticate = tokens.last().unwrap();
        let utf16 =
            |text: &str| -> Vec<u8> { text.encode_utf16().flat_map(u16::to_le_bytes).collect() };
        let contains = |needle: &[u8]| authenticate.windows(needle.len()).any(|w| w == needle);
        assert!(contains(&utf16("nosuchuser")));
        assert!(contains(&utf16("NOSUCHDOMAIN")));
    }

    // SOCKS 4 is given the address the dialer resolved and the user name.
    #[test]
    fn socks4_sends_the_resolved_address_and_user() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            let mut request = [0u8; 8 + 3];
            s.read_exact(&mut request).unwrap();
            assert_eq!(request, [4, 1, 1, 187, 10, 1, 2, 3, b'm', b'e', 0]);
            s.write_all(&[0, 0x5A, 0, 0, 0, 0, 0, 0]).unwrap();
        });
        let mut socks = proxy(port, named("me", "pw"));
        socks.kind = ProxyKind::Socks4;
        open_tunnel(&mut Local { port }, &socks, "example.invalid", 443, None).unwrap();
        server.join().unwrap();
    }
}
