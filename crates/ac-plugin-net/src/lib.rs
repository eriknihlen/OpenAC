//! The network plugins may use: HTTP/1.1 requests and TCP sockets (TLS
//! through the operating system's stack, as the launcher's), behind the
//! plugin's permissions and the hosts its manifest lists or the player
//! approved.
//!
//! Nothing here blocks the session: every request, connection and listener
//! runs on its own worker thread, and what they do comes back through
//! [`NetService::take_events`], which the session drains each pass and
//! hands to the plugin that asked. A socket's bytes wait in a buffer the
//! plugin reads with [`NetService::receive`]; what it sends is queued and
//! written by the worker.
//!
//! Requests, and connections a plugin opens through the proxy (its
//! WebSocket), follow the system proxy as OpenAC's plugins' `HttpClient`
//! and `ClientWebSocket` did; raw connections go direct (`proxied`).

mod http;
mod link;
pub mod local;
mod proxied;

use std::collections::{HashMap, VecDeque};
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use ac_plugin_abi::net::{HttpReply, HttpRequest, NetError, NetEvent, TcpConnect};
pub use ac_proxy::SystemProxy;
use ac_proxy::{ProxyKind, ProxyServer};

pub use http::Address;
use link::{ConnectError, Link};

/// A request's or connect's time limit when the plugin gives none.
const DEFAULT_TIMEOUT: Duration = Duration::from_secs(30);
/// The largest body accepted when the plugin gives no limit.
const DEFAULT_MAX_BYTES: usize = 16 * 1024 * 1024;
/// How many redirects a request follows.
const MAX_REDIRECTS: usize = 8;
/// The most bytes one socket holds unread; past it the worker stops
/// reading until the plugin catches up.
const MAX_BUFFERED: usize = 16 * 1024 * 1024;
/// The longest pause between looks at an idle socket.
const LONGEST_PAUSE: Duration = Duration::from_millis(10);

/// How long a closing socket keeps writing what the plugin queued before
/// it gives up (the .NET plugins flushed for up to 10 s when they stopped).
pub const CLOSE_FLUSH: Duration = Duration::from_secs(10);

/// Whether `host` may be reached: one of the manifest's hosts or one the
/// player approved, ignoring case.
#[must_use]
pub fn host_allowed(host: &str, allowed: &[String]) -> bool {
    allowed.iter().any(|a| a.eq_ignore_ascii_case(host))
}

/// The host a request or connect names, for the allow-list.
#[must_use]
pub fn request_host(url: &str) -> Option<String> {
    Address::parse(url).map(|a| a.host)
}

#[derive(Debug, Default)]
struct Buffers {
    incoming: VecDeque<u8>,
    outgoing: VecDeque<u8>,
    close: bool,
}

#[derive(Debug)]
struct Socket {
    owner: String,
    buffers: Arc<Mutex<Buffers>>,
}

#[derive(Debug)]
struct Listener {
    owner: String,
    stop: Arc<AtomicBool>,
}

type Events = Sender<(String, NetEvent)>;

/// One session's plugin network.
#[derive(Debug)]
pub struct NetService {
    next: u32,
    events: Events,
    received: Receiver<(String, NetEvent)>,
    accepted: Sender<(String, u32, TcpStream)>,
    accepted_received: Receiver<(String, u32, TcpStream)>,
    sockets: HashMap<u32, Socket>,
    listeners: HashMap<u32, Listener>,
    /// Requests in flight, by handle: their owner.
    requests: HashMap<u32, String>,
    /// Where requests go.
    proxy: SystemProxy,
}

impl Default for NetService {
    /// Following this machine's proxy settings, read once.
    fn default() -> Self {
        Self::with_proxy(SystemProxy::detect())
    }
}

impl NetService {
    /// A plugin network whose requests go where `proxy` says.
    #[must_use]
    pub fn with_proxy(proxy: SystemProxy) -> Self {
        let (events, received) = mpsc::channel();
        let (accepted, accepted_received) = mpsc::channel();
        Self {
            next: 0,
            events,
            received,
            accepted,
            accepted_received,
            sockets: HashMap::new(),
            listeners: HashMap::new(),
            requests: HashMap::new(),
            proxy,
        }
    }
}

fn lock(buffers: &Mutex<Buffers>) -> std::sync::MutexGuard<'_, Buffers> {
    buffers.lock().unwrap_or_else(PoisonError::into_inner)
}

impl NetService {
    fn handle(&mut self) -> u32 {
        self.next = self.next.wrapping_add(1).max(1);
        self.next
    }

    /// Sends a request for `owner` (its host already allowed); redirects
    /// are followed only to hosts in `allowed`. The handle; the reply
    /// arrives as [`NetEvent::HttpCompleted`].
    ///
    /// # Errors
    /// The address or request cannot be sent, or no thread could start.
    pub fn http_request(
        &mut self,
        owner: &str,
        request: HttpRequest,
        allowed: Vec<String>,
    ) -> Result<u32, String> {
        let address = Address::parse(&request.url)
            .ok_or_else(|| format!("'{}' is not an http(s) address", request.url))?;
        http::request_bytes(&request.method, &address, &request.headers, &request.body)
            .ok_or("the method or a header cannot be sent")?;
        let handle = self.handle();
        let events = self.events.clone();
        let owner_id = owner.to_owned();
        let proxy = self.proxy.clone();
        std::thread::Builder::new()
            .name(format!("plugin-http-{handle}"))
            .spawn(move || {
                let reply = fetch(handle, &request, address, &allowed, &proxy);
                let _ = events.send((owner_id, NetEvent::HttpCompleted(reply)));
            })
            .map_err(|e| format!("no worker thread: {e}"))?;
        self.requests.insert(handle, owner.to_owned());
        Ok(handle)
    }

    /// Opens a connection for `owner` (its host already allowed). The
    /// handle; [`NetEvent::Connected`] or [`NetEvent::Failed`] follows.
    /// `via_proxy` opens it as .NET's `ClientWebSocket` does, through the
    /// system proxy's tunnel for `wss` (with TLS) or `ws`; otherwise it goes
    /// direct, as a .NET socket does.
    ///
    /// # Errors
    /// The port is not one, or no thread could start.
    pub fn tcp_connect(
        &mut self,
        owner: &str,
        request: &TcpConnect,
        via_proxy: bool,
    ) -> Result<u32, String> {
        let port = u16::try_from(request.port)
            .ok()
            .filter(|p| *p != 0)
            .ok_or("the port is not 1 to 65535")?;
        if request.host.trim().is_empty() {
            return Err("no host".to_owned());
        }
        let handle = self.handle();
        let buffers = Arc::new(Mutex::new(Buffers::default()));
        let worker = Worker {
            handle,
            owner: owner.to_owned(),
            buffers: Arc::clone(&buffers),
            events: self.events.clone(),
        };
        let host = request.host.clone();
        let tls = request.tls;
        let timeout = timeout_of(request.timeout_ms);
        let proxy = via_proxy.then(|| self.proxy.clone());
        std::thread::Builder::new()
            .name(format!("plugin-socket-{handle}"))
            .spawn(move || {
                let deadline = Instant::now() + timeout;
                let connected = match &proxy {
                    None => link::connect(&host, port, tls, deadline),
                    Some(proxy) => {
                        let scheme = if tls { "wss" } else { "ws" };
                        proxied::route(proxy, scheme, &host, port, deadline).and_then(|route| {
                            proxied::connect(route, &host, port, tls, None, deadline)
                        })
                    }
                };
                match connected {
                    Ok(mut link) => {
                        link.stop_waiting();
                        worker.send(NetEvent::Connected(handle));
                        worker.run(link);
                    }
                    Err(error) => {
                        let (kind, text) = connect_error(error);
                        worker.send(NetEvent::Failed((handle, kind, text)));
                    }
                }
            })
            .map_err(|e| format!("no worker thread: {e}"))?;
        self.sockets.insert(
            handle,
            Socket {
                owner: owner.to_owned(),
                buffers,
            },
        );
        Ok(handle)
    }

    /// Listens on `port` of this computer only (loopback). The handle;
    /// each connection arrives as [`NetEvent::Accepted`].
    ///
    /// # Errors
    /// The port is taken or not one.
    pub fn tcp_listen(&mut self, owner: &str, port: u32) -> Result<u32, String> {
        let port = u16::try_from(port).map_err(|_| "the port is not 0 to 65535".to_owned())?;
        let listener = TcpListener::bind(("127.0.0.1", port))
            .map_err(|e| format!("cannot listen on {port}: {e}"))?;
        listener.set_nonblocking(true).map_err(|e| e.to_string())?;
        let handle = self.handle();
        let stop = Arc::new(AtomicBool::new(false));
        let watch = Arc::clone(&stop);
        let accepted = self.accepted.clone();
        let owner_id = owner.to_owned();
        std::thread::Builder::new()
            .name(format!("plugin-listen-{handle}"))
            .spawn(move || {
                let mut pause = Duration::from_millis(1);
                while !watch.load(Ordering::SeqCst) {
                    match listener.accept() {
                        Ok((stream, _)) => {
                            pause = Duration::from_millis(1);
                            if accepted.send((owner_id.clone(), handle, stream)).is_err() {
                                return;
                            }
                        }
                        Err(_) => {
                            std::thread::sleep(pause);
                            pause = (pause * 2).min(LONGEST_PAUSE * 5);
                        }
                    }
                }
            })
            .map_err(|e| format!("no worker thread: {e}"))?;
        self.listeners.insert(
            handle,
            Listener {
                owner: owner.to_owned(),
                stop,
            },
        );
        Ok(handle)
    }

    /// Queues `bytes` on one of `owner`'s sockets: how many.
    ///
    /// # Errors
    /// No such socket of the plugin's, or it is closed.
    pub fn send(&mut self, owner: &str, handle: u32, bytes: &[u8]) -> Result<u32, String> {
        let socket = self
            .sockets
            .get(&handle)
            .filter(|s| s.owner.eq_ignore_ascii_case(owner))
            .ok_or("no such socket")?;
        let mut buffers = lock(&socket.buffers);
        if buffers.close {
            return Err("the socket is closed".to_owned());
        }
        buffers.outgoing.extend(bytes);
        Ok(u32::try_from(bytes.len()).unwrap_or(u32::MAX))
    }

    /// Takes up to `max` waiting bytes from one of `owner`'s sockets.
    pub fn receive(&mut self, owner: &str, handle: u32, max: u32) -> Vec<u8> {
        let Some(socket) = self
            .sockets
            .get(&handle)
            .filter(|s| s.owner.eq_ignore_ascii_case(owner))
        else {
            return Vec::new();
        };
        let mut buffers = lock(&socket.buffers);
        let take = buffers
            .incoming
            .len()
            .min(usize::try_from(max).unwrap_or(usize::MAX));
        buffers.incoming.drain(..take).collect()
    }

    /// Closes one of `owner`'s sockets or listeners, or forgets a request
    /// (its reply is then dropped); false for a handle it does not hold.
    pub fn close(&mut self, owner: &str, handle: u32) -> bool {
        if let Some(socket) = self
            .sockets
            .get(&handle)
            .filter(|s| s.owner.eq_ignore_ascii_case(owner))
        {
            lock(&socket.buffers).close = true;
            self.sockets.remove(&handle);
            return true;
        }
        if let Some(listener) = self
            .listeners
            .get(&handle)
            .filter(|l| l.owner.eq_ignore_ascii_case(owner))
        {
            listener.stop.store(true, Ordering::SeqCst);
            self.listeners.remove(&handle);
            return true;
        }
        if self
            .requests
            .get(&handle)
            .is_some_and(|o| o.eq_ignore_ascii_case(owner))
        {
            self.requests.remove(&handle);
            return true;
        }
        false
    }

    /// `owner` stopped: its sockets and listeners close, its requests'
    /// replies are dropped.
    pub fn release(&mut self, owner: &str) {
        let mine = |o: &String| o.eq_ignore_ascii_case(owner);
        for socket in self.sockets.values().filter(|s| mine(&s.owner)) {
            lock(&socket.buffers).close = true;
        }
        self.sockets.retain(|_, s| !mine(&s.owner));
        for listener in self.listeners.values().filter(|l| mine(&l.owner)) {
            listener.stop.store(true, Ordering::SeqCst);
        }
        self.listeners.retain(|_, l| !mine(&l.owner));
        self.requests.retain(|_, o| !mine(o));
    }

    /// What happened since the last call, each with the plugin it is for:
    /// accepted connections get their handles here.
    pub fn take_events(&mut self) -> Vec<(String, NetEvent)> {
        let mut out = Vec::new();
        while let Ok((owner, listener, stream)) = self.accepted_received.try_recv() {
            if !self.listeners.contains_key(&listener) {
                continue;
            }
            let handle = self.handle();
            let buffers = Arc::new(Mutex::new(Buffers::default()));
            let worker = Worker {
                handle,
                owner: owner.clone(),
                buffers: Arc::clone(&buffers),
                events: self.events.clone(),
            };
            let started = stream.set_nonblocking(true).is_ok()
                && std::thread::Builder::new()
                    .name(format!("plugin-socket-{handle}"))
                    .spawn(move || {
                        let mut link = Link::Plain(link::Polling::from_accepted(stream));
                        link.stop_waiting();
                        worker.run(link);
                    })
                    .is_ok();
            if started {
                self.sockets.insert(
                    handle,
                    Socket {
                        owner: owner.clone(),
                        buffers,
                    },
                );
                out.push((owner, NetEvent::Accepted((listener, handle))));
            }
        }
        while let Ok((owner, event)) = self.received.try_recv() {
            let live = match &event {
                NetEvent::HttpCompleted(reply) => self.requests.remove(&reply.handle).is_some(),
                // A closed socket keeps its unread bytes until the plugin
                // closes it too.
                NetEvent::Connected(h) | NetEvent::Readable(h) | NetEvent::Closed((h, _)) => {
                    self.sockets.contains_key(h)
                }
                NetEvent::Failed((h, _, _)) => self.sockets.remove(h).is_some(),
                NetEvent::Accepted(_) => true,
            };
            if live {
                out.push((owner, event));
            }
        }
        out
    }
}

fn timeout_of(ms: u32) -> Duration {
    if ms == 0 {
        DEFAULT_TIMEOUT
    } else {
        Duration::from_millis(u64::from(ms))
    }
}

fn connect_error(error: ConnectError) -> (NetError, String) {
    match error {
        ConnectError::Timeout => (NetError::Timeout, "the connection timed out".to_owned()),
        ConnectError::Connect(text) => (NetError::Connect, text),
        ConnectError::Tls(text) => (NetError::Tls, text),
    }
}

fn failure(handle: u32, error: NetError, message: String) -> HttpReply {
    HttpReply {
        handle,
        error,
        message,
        ..HttpReply::default()
    }
}

fn read_failure(handle: u32, error: http::ReadError, max_bytes: usize) -> HttpReply {
    match error {
        http::ReadError::TooLarge => failure(
            handle,
            NetError::TooLarge,
            format!("the reply is over {max_bytes} bytes"),
        ),
        http::ReadError::Protocol(text) => failure(handle, NetError::Protocol, text),
        http::ReadError::Io(e) if e.kind() == std::io::ErrorKind::TimedOut => failure(
            handle,
            NetError::Timeout,
            "the request timed out".to_owned(),
        ),
        http::ReadError::Io(e) => failure(
            handle,
            NetError::Connect,
            format!("reading the reply failed: {e}"),
        ),
    }
}

fn invalid(handle: u32) -> HttpReply {
    failure(
        handle,
        NetError::Invalid,
        "the request cannot be sent".to_owned(),
    )
}

/// One request, following redirects to allowed hosts, each hop where the
/// system proxy sends it.
fn fetch(
    handle: u32,
    request: &HttpRequest,
    mut address: Address,
    allowed: &[String],
    proxy: &SystemProxy,
) -> HttpReply {
    let deadline = Instant::now() + timeout_of(request.timeout_ms);
    let max_bytes = if request.max_bytes == 0 {
        DEFAULT_MAX_BYTES
    } else {
        usize::try_from(request.max_bytes).unwrap_or(usize::MAX)
    };
    let head_only = request.method.eq_ignore_ascii_case("HEAD");
    let mut method = request.method.clone();
    let mut body = request.body.clone();
    for _ in 0..=MAX_REDIRECTS {
        let scheme = if address.https { "https" } else { "http" };
        let proxies = match proxied::route(proxy, scheme, &address.host, address.port, deadline) {
            Ok(proxies) => proxies,
            Err(error) => {
                let (kind, text) = connect_error(error);
                return failure(handle, kind, text);
            }
        };
        // One try through one proxy (or none); a failure says whether the
        // proxy could not be connected to, so the next one is tried.
        let attempt = |server: Option<&ProxyServer>| -> Result<http::Response, (HttpReply, bool)> {
            let fail = |error| {
                let (kind, text) = connect_error(error);
                failure(handle, kind, text)
            };
            match server {
                // Plain HTTP through an HTTP proxy: the request goes to it.
                Some(server)
                    if !address.https
                        && matches!(server.kind, ProxyKind::Http | ProxyKind::Https) =>
                {
                    let outgoing = proxied::Outgoing {
                        method: &method,
                        address: &address,
                        headers: &request.headers,
                        body: &body,
                        max_bytes,
                        head_only,
                    };
                    proxied::through_http_proxy(server, &outgoing, deadline).map_err(|failed| {
                        match failed {
                            proxied::Failure::Unreachable(error) => (fail(error), true),
                            proxied::Failure::Connect(error) => (fail(error), false),
                            proxied::Failure::Read(error) => {
                                (read_failure(handle, error, max_bytes), false)
                            }
                            proxied::Failure::Invalid => (invalid(handle), false),
                            proxied::Failure::Auth(text) => {
                                (failure(handle, NetError::Connect, text), false)
                            }
                        }
                    })
                }
                server => {
                    let Some(bytes) =
                        http::request_bytes(&method, &address, &request.headers, &body)
                    else {
                        return Err((invalid(handle), false));
                    };
                    let user_agent = http::user_agent(&request.headers);
                    let mut link = proxied::connect_once(
                        server,
                        &address.host,
                        address.port,
                        address.https,
                        user_agent,
                        deadline,
                    )
                    .map_err(|(error, unreachable)| (fail(error), unreachable))?;
                    if let Err(e) = link.write_all(&bytes).and_then(|()| link.flush()) {
                        return Err((
                            failure(
                                handle,
                                NetError::Connect,
                                format!("sending the request failed: {e}"),
                            ),
                            false,
                        ));
                    }
                    let mut reader = std::io::BufReader::new(link);
                    http::read_response(&mut reader, max_bytes, head_only)
                        .map(|(response, _)| response)
                        .map_err(|error| (read_failure(handle, error, max_bytes), false))
                }
            }
        };
        let response = match proxies.try_each(attempt, |(_, unreachable)| *unreachable) {
            Ok(response) => response,
            Err((reply, _)) => return reply,
        };
        let redirect = matches!(response.status, 301 | 302 | 303 | 307 | 308);
        if request.follow_redirects
            && redirect
            && let Some(next) = response.header("location").and_then(|l| address.join(l))
        {
            if !host_allowed(&next.host, allowed) {
                return failure(
                    handle,
                    NetError::Refused,
                    format!("the redirect to {} is to a host not approved", next.host),
                );
            }
            if response.status == 303
                || ((response.status == 301 || response.status == 302)
                    && method.eq_ignore_ascii_case("POST"))
            {
                method = "GET".to_owned();
                body.clear();
            }
            address = next;
            continue;
        }
        return HttpReply {
            handle,
            status: u32::from(response.status),
            reason: response.reason,
            headers: response.headers,
            body: response.body,
            error: NetError::None,
            message: String::new(),
        };
    }
    failure(handle, NetError::Protocol, "too many redirects".to_owned())
}

/// A socket's worker: writes what the plugin queued, reads what arrives.
struct Worker {
    handle: u32,
    owner: String,
    buffers: Arc<Mutex<Buffers>>,
    events: Events,
}

impl Worker {
    fn send(&self, event: NetEvent) {
        // The session may have ended; nothing to tell then.
        let _ = self.events.send((self.owner.clone(), event));
    }

    fn run(&self, mut link: Link) {
        let mut buffer = vec![0u8; 64 * 1024];
        let mut pause = Duration::from_millis(1);
        let reason = loop {
            let (outgoing, close, room) = {
                let mut b = lock(&self.buffers);
                let outgoing: Vec<u8> = b.outgoing.drain(..).collect();
                (outgoing, b.close, b.incoming.len() < MAX_BUFFERED)
            };
            if close {
                // Closing writes what was queued before the close, for up
                // to `CLOSE_FLUSH`, then ends the sending half so the
                // other end reads everything and then the end.
                if !outgoing.is_empty() {
                    link.set_write_deadline(Some(Instant::now() + CLOSE_FLUSH));
                    let _ = link.write_all(&outgoing).and_then(|()| link.flush());
                }
                link.shutdown();
                return;
            }
            let mut busy = false;
            if !outgoing.is_empty() {
                if let Err(e) = link.write_all(&outgoing).and_then(|()| link.flush()) {
                    break e.to_string();
                }
                busy = true;
            }
            if room {
                match link.read(&mut buffer) {
                    Ok(0) => break String::new(),
                    Ok(n) => {
                        let was_empty = {
                            let mut b = lock(&self.buffers);
                            let was_empty = b.incoming.is_empty();
                            b.incoming.extend(&buffer[..n]);
                            was_empty
                        };
                        if was_empty {
                            self.send(NetEvent::Readable(self.handle));
                        }
                        busy = true;
                    }
                    Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {}
                    Err(e) => break e.to_string(),
                }
            }
            if busy {
                pause = Duration::from_millis(1);
            } else {
                std::thread::sleep(pause);
                pause = (pause * 2).min(LONGEST_PAUSE);
            }
        };
        lock(&self.buffers).close = true;
        self.send(NetEvent::Closed((self.handle, reason)));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn drain_until(service: &mut NetService, want: usize) -> Vec<(String, NetEvent)> {
        let start = Instant::now();
        let mut events = Vec::new();
        while events.len() < want && start.elapsed() < Duration::from_secs(10) {
            events.extend(service.take_events());
            std::thread::sleep(Duration::from_millis(5));
        }
        events
    }

    // A plugin's HTTP request against a server on this computer: the
    // reply (a chunked body after a redirect on the same host) comes back
    // to the plugin that asked; a redirect to a host not approved fails.
    #[test]
    fn a_request_comes_back_to_its_plugin_through_a_redirect() {
        let server = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = server.local_addr().unwrap().port();
        std::thread::spawn(move || {
            for (index, stream) in server.incoming().take(3).enumerate() {
                let mut stream = stream.unwrap();
                let mut head = Vec::new();
                let mut byte = [0u8; 1];
                while !head.ends_with(b"\r\n\r\n") && stream.read(&mut byte).unwrap() == 1 {
                    head.push(byte[0]);
                }
                let reply: &[u8] = match index {
                    0 => b"HTTP/1.1 302 Found\r\nLocation: /final\r\nContent-Length: 0\r\n\r\n",
                    1 => b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nX-A: b\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n",
                    _ => b"HTTP/1.1 301 Moved\r\nLocation: http://elsewhere.invalid/\r\nContent-Length: 0\r\n\r\n",
                };
                stream.write_all(reply).unwrap();
            }
        });
        let mut service = NetService::default();
        let allowed = vec!["127.0.0.1".to_owned()];
        let request = HttpRequest {
            method: "GET".into(),
            url: format!("http://127.0.0.1:{port}/start"),
            follow_redirects: true,
            ..HttpRequest::default()
        };
        let handle = service
            .http_request("openac.hello", request.clone(), allowed.clone())
            .unwrap();
        let events = drain_until(&mut service, 1);
        let [(owner, NetEvent::HttpCompleted(reply))] = events.as_slice() else {
            panic!("one reply, got {events:?}");
        };
        assert_eq!(owner, "openac.hello");
        assert_eq!(reply.handle, handle);
        assert_eq!((reply.status, reply.error), (200, NetError::None));
        assert_eq!(reply.body, b"hello world");
        assert!(reply.headers.contains(&("X-A".to_owned(), "b".to_owned())));
        service
            .http_request("openac.hello", request, allowed)
            .unwrap();
        let events = drain_until(&mut service, 1);
        let [(_, NetEvent::HttpCompleted(reply))] = events.as_slice() else {
            panic!("one reply, got {events:?}");
        };
        assert_eq!(reply.error, NetError::Refused);
    }

    // A socket: connected, an echo read back through the buffer, closed
    // by the other end; a listener hands an accepted connection to its
    // plugin; release closes what a plugin held.
    #[test]
    fn a_socket_connects_echoes_and_closes() {
        let mut service = NetService::default();
        let listener = service.tcp_listen("openac.a", 0);
        assert!(listener.is_ok());
        let server = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = server.local_addr().unwrap().port();
        std::thread::spawn(move || {
            let (mut stream, _) = server.accept().unwrap();
            let mut buf = [0u8; 4];
            stream.read_exact(&mut buf).unwrap();
            stream.write_all(&buf).unwrap();
        });
        let handle = service
            .tcp_connect(
                "openac.a",
                &TcpConnect {
                    host: "127.0.0.1".into(),
                    port: u32::from(port),
                    tls: false,
                    timeout_ms: 5_000,
                },
                false,
            )
            .unwrap();
        let events = drain_until(&mut service, 1);
        assert_eq!(events[0].1, NetEvent::Connected(handle));
        assert!(service.send("openac.b", handle, b"ping").is_err());
        assert_eq!(service.send("openac.a", handle, b"ping"), Ok(4));
        // The echo, then the other end closing; the bytes stay readable.
        let events = drain_until(&mut service, 2);
        assert!(events.iter().any(|(_, e)| *e == NetEvent::Readable(handle)));
        assert!(
            events
                .iter()
                .any(|(_, e)| matches!(e, NetEvent::Closed((h, _)) if *h == handle))
        );
        assert_eq!(service.receive("openac.a", handle, 64), b"ping");
        service.release("openac.a");
        assert!(!service.close("openac.a", listener.unwrap()));
    }

    // Closing a socket right after queueing bytes still delivers them: the
    // worker writes what was queued (for up to `CLOSE_FLUSH`, as the .NET
    // plugins flushed on stop), then ends its sending half, so the other end
    // reads every byte and then the end.
    #[test]
    fn closing_a_socket_flushes_what_was_queued() {
        let server = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = server.local_addr().unwrap().port();
        let (got, received) = mpsc::channel();
        std::thread::spawn(move || {
            let (mut stream, _) = server.accept().unwrap();
            let mut all = Vec::new();
            stream.read_to_end(&mut all).unwrap();
            got.send(all).unwrap();
        });
        let mut service = NetService::default();
        let handle = service
            .tcp_connect(
                "openac.a",
                &TcpConnect {
                    host: "127.0.0.1".into(),
                    port: u32::from(port),
                    tls: false,
                    timeout_ms: 5_000,
                },
                false,
            )
            .unwrap();
        let events = drain_until(&mut service, 1);
        assert_eq!(events[0].1, NetEvent::Connected(handle));
        let payload: Vec<u8> = (0..4 * 1024 * 1024u32).map(|i| (i % 251) as u8).collect();
        assert_eq!(
            service.send("openac.a", handle, &payload),
            Ok(u32::try_from(payload.len()).unwrap())
        );
        assert!(service.close("openac.a", handle));
        let all = received.recv_timeout(Duration::from_secs(10)).unwrap();
        assert_eq!(all.len(), payload.len());
        assert!(all == payload);
    }

    fn through(port: u16, credentials: Option<ac_proxy::Credentials>) -> NetService {
        NetService::with_proxy(SystemProxy::fixed(ac_proxy::ProxyServer {
            kind: ProxyKind::Http,
            host: "127.0.0.1".into(),
            port,
            credentials,
        }))
    }

    /// A request head off a fake proxy's connection.
    fn head_of(stream: &mut TcpStream) -> String {
        let mut head = Vec::new();
        let mut byte = [0u8; 1];
        while !head.ends_with(b"\r\n\r\n") && stream.read(&mut byte).unwrap() == 1 {
            head.push(byte[0]);
        }
        String::from_utf8(head).unwrap()
    }

    fn one_reply(service: &mut NetService) -> HttpReply {
        let events = drain_until(service, 1);
        let [(_, NetEvent::HttpCompleted(reply))] = events.as_slice() else {
            panic!("one reply, got {events:?}");
        };
        reply.clone()
    }

    // .NET `HttpClient` through an HTTP proxy for an `https` address: a
    // `CONNECT` tunnel carrying the request's User-Agent, then TLS to the
    // server inside it (the proxy sees a handshake record, not the request).
    #[test]
    fn an_https_request_goes_through_the_proxys_tunnel() {
        let fake = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = fake.local_addr().unwrap().port();
        let proxy = std::thread::spawn(move || {
            let (mut s, _) = fake.accept().unwrap();
            let head = head_of(&mut s);
            assert!(
                head.starts_with(
                    "CONNECT example.invalid:443 HTTP/1.1\r\nHost: example.invalid:443\r\nUser-Agent: plugin/1\r\n"
                ),
                "{head}"
            );
            s.write_all(b"HTTP/1.1 200 Connection established\r\n\r\n")
                .unwrap();
            let mut record = [0u8; 1];
            s.read_exact(&mut record).unwrap();
            record[0]
        });
        let mut service = through(port, None);
        let request = HttpRequest {
            method: "GET".into(),
            url: "https://example.invalid/feed".into(),
            headers: vec![("User-Agent".into(), "plugin/1".into())],
            timeout_ms: 20_000,
            ..HttpRequest::default()
        };
        service
            .http_request("openac.a", request, Vec::new())
            .unwrap();
        let reply = one_reply(&mut service);
        assert_eq!(proxy.join().unwrap(), 0x16, "a TLS handshake record");
        assert_eq!(reply.error, NetError::Tls, "{}", reply.message);
    }

    // RFC 9112 3.2.2 and .NET: a plain `http` request goes to the proxy
    // with the whole address as its target and the connection kept; the
    // proxy's 407 is answered on that connection with the credentials of
    // its address; without credentials the 407 is the plugin's reply.
    #[test]
    fn a_plain_request_goes_to_the_proxy_and_answers_its_challenge() {
        let fake = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = fake.local_addr().unwrap().port();
        let proxy = std::thread::spawn(move || {
            let (mut s, _) = fake.accept().unwrap();
            let head = head_of(&mut s);
            assert!(
                head.starts_with(
                    "GET http://example.invalid:8080/p?q=1 HTTP/1.1\r\nHost: example.invalid:8080\r\n"
                ),
                "{head}"
            );
            assert!(!head.contains("Connection: close"), "{head}");
            s.write_all(
                b"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 4\r\n\r\nnope",
            )
            .unwrap();
            let head = head_of(&mut s);
            assert!(
                head.contains("\r\nProxy-Authorization: Basic bWU6cHc=\r\n"),
                "{head}"
            );
            s.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok")
                .unwrap();
            let (mut s, _) = fake.accept().unwrap();
            head_of(&mut s);
            s.write_all(
                b"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 0\r\n\r\n",
            )
            .unwrap();
        });
        let request = HttpRequest {
            method: "GET".into(),
            url: "http://example.invalid:8080/p?q=1".into(),
            timeout_ms: 20_000,
            ..HttpRequest::default()
        };
        let mut service = through(
            port,
            Some(ac_proxy::Credentials::Named {
                domain: None,
                user: "me".into(),
                password: "pw".into(),
            }),
        );
        service
            .http_request("openac.a", request.clone(), Vec::new())
            .unwrap();
        let reply = one_reply(&mut service);
        assert_eq!(
            (reply.status, reply.body.as_slice()),
            (200, b"ok".as_slice()),
            "{}",
            reply.message
        );
        let mut bare = through(port, None);
        bare.http_request("openac.a", request, Vec::new()).unwrap();
        let reply = one_reply(&mut bare);
        assert_eq!((reply.status, reply.error), (407, NetError::None));
        proxy.join().unwrap();
    }

    // .NET `SendAsyncMultiProxy` over a Windows list: the first proxy
    // cannot be connected to (nothing listens on its port), so the request
    // goes to the second.
    #[test]
    fn a_request_falls_over_to_the_next_proxy_of_a_list() {
        let closed = TcpListener::bind("127.0.0.1:0")
            .unwrap()
            .local_addr()
            .unwrap()
            .port();
        let fake = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = fake.local_addr().unwrap().port();
        let proxy = std::thread::spawn(move || {
            let (mut s, _) = fake.accept().unwrap();
            let head = head_of(&mut s);
            s.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok")
                .unwrap();
            head
        });
        let mut service = NetService::with_proxy(SystemProxy::listed(&format!(
            "127.0.0.1:{closed};127.0.0.1:{port}"
        )));
        let request = HttpRequest {
            method: "GET".into(),
            url: "http://example.invalid/p".into(),
            timeout_ms: 20_000,
            ..HttpRequest::default()
        };
        service
            .http_request("openac.a", request, Vec::new())
            .unwrap();
        let reply = one_reply(&mut service);
        assert_eq!(
            (reply.status, reply.body.as_slice()),
            (200, b"ok".as_slice()),
            "{}",
            reply.message
        );
        let head = proxy.join().unwrap();
        assert!(
            head.starts_with("GET http://example.invalid/p HTTP/1.1\r\n"),
            "{head}"
        );
    }

    // .NET 10 reads no `socks=` entry from the Windows settings: a
    // WebSocket's connection goes through the list's HTTP proxy.
    #[test]
    fn a_websocket_connection_passes_over_a_socks_entry_of_a_list() {
        let socks = TcpListener::bind("127.0.0.1:0").unwrap();
        let socks_port = socks.local_addr().unwrap().port();
        let fake = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = fake.local_addr().unwrap().port();
        let proxy = std::thread::spawn(move || {
            let (mut s, _) = fake.accept().unwrap();
            let head = head_of(&mut s);
            s.write_all(b"HTTP/1.1 200 Connection established\r\n\r\n")
                .unwrap();
            head
        });
        let mut service = NetService::with_proxy(SystemProxy::listed(&format!(
            "socks=127.0.0.1:{socks_port} 127.0.0.1:{port}"
        )));
        let connect = TcpConnect {
            host: "ws.example.invalid".into(),
            port: 80,
            tls: false,
            timeout_ms: 20_000,
        };
        let handle = service.tcp_connect("openac.a", &connect, true).unwrap();
        let events = drain_until(&mut service, 1);
        assert_eq!(events[0].1, NetEvent::Connected(handle));
        let head = proxy.join().unwrap();
        assert!(
            head.starts_with("CONNECT ws.example.invalid:80 HTTP/1.1\r\n"),
            "{head}"
        );
        socks.set_nonblocking(true).unwrap();
        assert!(socks.accept().is_err(), "the SOCKS entry is not used");
    }

    // A connection opened as a WebSocket's goes through the proxy's tunnel
    // (`ws`: `CONNECT` to the server's port, then the plugin's bytes);
    // `tcp-connect` itself never asks the proxy, as a .NET socket does not.
    #[test]
    fn a_websocket_connection_goes_through_the_proxy_and_a_raw_one_does_not() {
        let fake = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = fake.local_addr().unwrap().port();
        let proxy = std::thread::spawn(move || {
            let (mut s, _) = fake.accept().unwrap();
            let head = head_of(&mut s);
            assert!(
                head.starts_with("CONNECT ws.example.invalid:80 HTTP/1.1\r\n"),
                "{head}"
            );
            s.write_all(b"HTTP/1.1 200 Connection established\r\n\r\n")
                .unwrap();
            let mut hello = [0u8; 5];
            s.read_exact(&mut hello).unwrap();
            s.write_all(&hello).unwrap();
        });
        let mut service = through(port, None);
        let connect = TcpConnect {
            host: "ws.example.invalid".into(),
            port: 80,
            tls: false,
            timeout_ms: 20_000,
        };
        let handle = service.tcp_connect("openac.a", &connect, true).unwrap();
        let events = drain_until(&mut service, 1);
        assert_eq!(events[0].1, NetEvent::Connected(handle));
        service.send("openac.a", handle, b"hello").unwrap();
        let events = drain_until(&mut service, 1);
        assert_eq!(events[0].1, NetEvent::Readable(handle));
        assert_eq!(service.receive("openac.a", handle, 16), b"hello");
        proxy.join().unwrap();
        let raw = service.tcp_connect("openac.a", &connect, false).unwrap();
        let events = drain_until(&mut service, 1);
        assert!(
            matches!(&events[0].1, NetEvent::Failed((h, NetError::Connect, _)) if *h == raw),
            "{events:?}"
        );
    }
}
