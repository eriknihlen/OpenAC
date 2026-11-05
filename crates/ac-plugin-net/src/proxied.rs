//! Plugins' requests through the system proxy, as OpenAC's plugins reached
//! the network with .NET's `HttpClient` and `ClientWebSocket` (both follow
//! `HttpClient.DefaultProxy`; `ac-proxy` finds it and opens the tunnel):
//!
//! - an `https` request, or any request through a SOCKS proxy, goes through
//!   a tunnel to the server, TLS to the server inside it;
//! - a plain `http` request through an HTTP proxy is sent to the proxy with
//!   the whole address as its target, answering the proxy's `407`;
//! - a WebSocket's connection (`tcp-connect` through the proxy) is a tunnel
//!   too, as .NET opens one for `ws` and `wss`.
//!
//! Raw sockets (`tcp-connect`) go direct, as a .NET `Socket` or `TcpClient`
//! does.

use std::io::{BufRead, BufReader, Write};
use std::net::{Ipv4Addr, ToSocketAddrs};
use std::time::Instant;

use ac_proxy::{
    Dialer, Proxies, ProxyAuth, ProxyKind, ProxyServer, ProxyStream, Step, SystemProxy, Target,
    TunnelError, open_tunnel,
};

use crate::http::{self, Address, ReadError, Response};
use crate::link::{self, ConnectError, Link};

/// Reaches the proxy within the request's deadline: a new connection (over
/// TLS to an `https` proxy).
struct PluginDialer<'a> {
    proxy: &'a ProxyServer,
    deadline: Instant,
    /// The proxy could not be connected to (not timed out): the next proxy
    /// of a list is tried (.NET's `RequestRetryType.RetryOnNextProxy`).
    unreachable: bool,
}

impl<'a> PluginDialer<'a> {
    fn new(proxy: &'a ProxyServer, deadline: Instant) -> Self {
        Self {
            proxy,
            deadline,
            unreachable: false,
        }
    }
}

impl Dialer for PluginDialer<'_> {
    type Stream = Link;
    type Error = ConnectError;

    fn dial(&mut self) -> Result<Link, ConnectError> {
        let host = self.proxy.bare_host();
        let link = link::connect_tcp(host, self.proxy.port, self.deadline)
            .inspect_err(|error| self.unreachable = matches!(error, ConnectError::Connect(_)))?;
        if self.proxy.kind == ProxyKind::Https {
            link::secure(link, host)
        } else {
            Ok(link)
        }
    }

    fn resolve_v4(&mut self, host: &str, port: u16) -> Result<Ipv4Addr, ConnectError> {
        let addresses = (host, port)
            .to_socket_addrs()
            .map_err(|e| ConnectError::Connect(format!("{host}: {e}")))?;
        addresses
            .into_iter()
            .find_map(|address| match address.ip() {
                std::net::IpAddr::V4(v4) => Some(v4),
                std::net::IpAddr::V6(_) => None,
            })
            .ok_or_else(|| {
                ConnectError::Connect(format!(
                    "{host} has no IPv4 address, which a SOCKS 4 proxy needs"
                ))
            })
    }
}

fn tunnel_error(proxy: &ProxyServer, error: TunnelError<ConnectError>) -> ConnectError {
    match error {
        TunnelError::Dial(error) => error,
        TunnelError::Io(error) if error.kind() == std::io::ErrorKind::TimedOut => {
            ConnectError::Timeout
        }
        TunnelError::Io(error) => {
            ConnectError::Connect(format!("talking to the proxy {proxy} failed: {error}"))
        }
        TunnelError::Refused(text) => ConnectError::Connect(text),
    }
}

/// Where a connection for `scheme://host:port` goes: `Err` names a setting
/// that cannot be followed, or the deadline passed while the proxy was
/// looked up (a configuration script may take a while).
pub(crate) fn route(
    proxy: &SystemProxy,
    scheme: &str,
    host: &str,
    port: u16,
    deadline: Instant,
) -> Result<Proxies, ConnectError> {
    let host_part = if host.contains(':') {
        format!("[{host}]")
    } else {
        host.to_owned()
    };
    let url = format!("{scheme}://{host_part}:{port}/");
    let route = proxy
        .route(&Target::new(scheme, host, &url))
        .map_err(ConnectError::Connect)?;
    if Instant::now() >= deadline {
        return Err(ConnectError::Timeout);
    }
    Ok(route)
}

/// A connection to `host:port` (TLS to it with `tls`), through a tunnel of
/// the first of `proxies` that can be connected to, or direct when there is
/// none.
pub(crate) fn connect(
    proxies: Proxies,
    host: &str,
    port: u16,
    tls: bool,
    user_agent: Option<&str>,
    deadline: Instant,
) -> Result<Link, ConnectError> {
    proxies
        .try_each(
            |proxy| connect_once(proxy, host, port, tls, user_agent, deadline),
            |(_, unreachable)| *unreachable,
        )
        .map_err(|(error, _)| error)
}

/// [`connect`] through one proxy (or none); the failure says whether the
/// proxy could not be connected to.
pub(crate) fn connect_once(
    proxy: Option<&ProxyServer>,
    host: &str,
    port: u16,
    tls: bool,
    user_agent: Option<&str>,
    deadline: Instant,
) -> Result<Link, (ConnectError, bool)> {
    let Some(proxy) = proxy else {
        return link::connect(host, port, tls, deadline).map_err(|error| (error, false));
    };
    let mut dialer = PluginDialer::new(proxy, deadline);
    let tunnel = open_tunnel(&mut dialer, proxy, host, port, user_agent)
        .map_err(|error| (tunnel_error(proxy, error), dialer.unreachable))?;
    if tls {
        link::secure(tunnel, host).map_err(|error| (error, false))
    } else {
        Ok(tunnel)
    }
}

/// A plain `http` request as it goes out.
#[derive(Debug, Clone, Copy)]
pub(crate) struct Outgoing<'a> {
    pub method: &'a str,
    pub address: &'a Address,
    pub headers: &'a [(String, String)],
    pub body: &'a [u8],
    pub max_bytes: usize,
    pub head_only: bool,
}

/// Why a request through an HTTP proxy got no response.
#[derive(Debug)]
pub(crate) enum Failure {
    /// The proxy could not be connected to: the next one of a list is
    /// tried.
    Unreachable(ConnectError),
    Connect(ConnectError),
    Read(ReadError),
    Invalid,
    Auth(String),
}

/// A new connection to the proxy, saying whether it could not be reached.
fn dial(dialer: &mut PluginDialer<'_>) -> Result<Link, Failure> {
    dialer.dial().map_err(|error| {
        if dialer.unreachable {
            Failure::Unreachable(error)
        } else {
            Failure::Connect(error)
        }
    })
}

/// A plain `http` request sent to an HTTP proxy: a `407` is answered with
/// the proxy's credentials (on the same connection while the proxy keeps
/// it open); one that cannot be satisfied is the response. A request on a
/// kept connection that the proxy closed before answering is sent again on
/// a new one.
pub(crate) fn through_http_proxy(
    proxy: &ProxyServer,
    request: &Outgoing<'_>,
    deadline: Instant,
) -> Result<Response, Failure> {
    let Outgoing {
        method,
        address,
        headers,
        body,
        max_bytes,
        head_only,
    } = *request;
    let mut dialer = PluginDialer::new(proxy, deadline);
    let mut auth = ProxyAuth::new(proxy).with_body(body);
    let mut authorization: Option<String> = None;
    let mut reader = BufReader::new(dial(&mut dialer)?);
    let mut reused = false;
    loop {
        let bytes =
            http::proxy_request_bytes(method, address, headers, body, authorization.as_deref())
                .ok_or(Failure::Invalid)?;
        let sent = reader
            .get_mut()
            .write_all(&bytes)
            .and_then(|()| reader.get_mut().flush());
        // Nothing came back on a kept connection: the proxy closed it.
        let silent = sent.is_err() || !reader.fill_buf().is_ok_and(|bytes| !bytes.is_empty());
        if reused && silent {
            reader = BufReader::new(dial(&mut dialer)?);
            reused = false;
            continue;
        }
        if let Err(error) = sent {
            return Err(Failure::Read(ReadError::Io(error)));
        }
        let (response, persistent) =
            http::read_response(&mut reader, max_bytes, head_only).map_err(Failure::Read)?;
        let challenges = || {
            response
                .headers
                .iter()
                .filter(|(name, _)| name.eq_ignore_ascii_case("proxy-authenticate"))
                .map(|(_, value)| value.as_str())
        };
        if response.status != 407 {
            if authorization.is_some() {
                auth.answered(challenges()).map_err(Failure::Auth)?;
            }
            return Ok(response);
        }
        let endpoint = reader.get_ref().channel_binding();
        match auth.challenged(challenges(), method, &address.target, endpoint.as_deref()) {
            Step::Send(value) => authorization = Some(value),
            Step::Stop(_) => return Ok(response),
            Step::Fail(text) => return Err(Failure::Auth(text)),
        }
        if persistent {
            reused = true;
        } else {
            reader = BufReader::new(dial(&mut dialer)?);
            reused = false;
        }
    }
}
