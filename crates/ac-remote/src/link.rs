//! The link: one worker thread that owns the connection to the relay.
//!
//! It connects (TCP, TLS for `wss`, the WebSocket handshake with the bearer
//! token), sends the hello, waits for `hello_ok`, then carries control
//! messages and binary frames both ways until the link is lost; then it
//! reconnects with backoff. The caller sees [`Inbound`] events and queues
//! messages with [`Link::send_control`] and [`Link::send_binary`]; it never
//! waits on the network. Nothing the relay sends can panic the worker: every
//! problem ends the connection with a [`LinkLoss`] and a reconnect.

use std::collections::VecDeque;
use std::fmt;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::mpsc::{self, Receiver, RecvError, RecvTimeoutError, Sender, TryRecvError};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use crate::net::{self, Polling, Transport};
use crate::protocol::{
    EndpointMessage, Hello, MessageError, PROTOCOL_VERSION, RefuseReason, RelayMessage, Status,
};
use crate::secret::Secret;
use crate::url::RelayUrl;
use crate::ws::{Handshake, HandshakeError, WsClient, WsError, WsEvent, WsLimits};

/// The link's timings. The defaults are the relay contract's; tests shorten
/// them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Timing {
    /// No frame from the relay for this long is a lost link.
    pub silence: Duration,
    /// The relay must answer the hello within this.
    pub hello_timeout: Duration,
    /// Connecting (name lookup, TCP, TLS and the WebSocket handshake) must
    /// finish within this.
    pub connect_timeout: Duration,
    /// The first wait before reconnecting; it doubles after each failure and
    /// starts over once a connection has stayed up for `stable_after`.
    pub backoff_first: Duration,
    /// The longest wait before reconnecting; also the wait after the relay
    /// says a newer connection replaced this one.
    pub backoff_max: Duration,
    /// A connection up this long after its `hello_ok` counts as good: the
    /// backoff starts over. A shorter one does not, so two processes that
    /// keep replacing each other back off.
    pub stable_after: Duration,
}

impl Default for Timing {
    fn default() -> Self {
        Self {
            silence: Duration::from_secs(15),
            hello_timeout: Duration::from_secs(10),
            connect_timeout: Duration::from_secs(10),
            backoff_first: Duration::from_secs(1),
            backoff_max: Duration::from_secs(60),
            stable_after: Duration::from_secs(15),
        }
    }
}

/// The default bound on queued outbound bytes.
pub const DEFAULT_QUEUE_BYTES: usize = 16 * 1024 * 1024;

/// The default bound on bytes held in events the caller has not taken yet.
pub const DEFAULT_INBOUND_BYTES: usize = 4 * 1024 * 1024;

/// What the link needs.
#[derive(Debug, Clone)]
pub struct LinkConfig {
    /// The relay.
    pub url: RelayUrl,
    /// The session's token, sent as `Authorization: Bearer`.
    pub token: Secret,
    /// The session's state as the first hello tells it; keep it current with
    /// [`Link::update_hello`].
    pub hello: Hello,
    /// The timings.
    pub timing: Timing,
    /// The frame size limits.
    pub limits: WsLimits,
    /// The most bytes queued for sending; past it [`Link::send_binary`]
    /// refuses.
    pub queue_bytes: usize,
    /// The most payload bytes held in events the caller has not taken yet;
    /// past it the link drops the connection ([`LinkLoss::InboundOverflow`]).
    pub inbound_bytes: usize,
}

impl LinkConfig {
    /// A configuration with the contract's timings and limits.
    #[must_use]
    pub fn new(url: RelayUrl, token: Secret, hello: Hello) -> Self {
        Self {
            url,
            token,
            hello,
            timing: Timing::default(),
            limits: WsLimits::default(),
            queue_bytes: DEFAULT_QUEUE_BYTES,
            inbound_bytes: DEFAULT_INBOUND_BYTES,
        }
    }
}

/// Why a connection ended or could not be made. No variant carries the
/// token. The only text from the relay is a refusal reason this version does
/// not know ([`RefuseReason::Other`]), cut to
/// [`MAX_RELAY_WORD`](crate::protocol::MAX_RELAY_WORD) characters.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum LinkLoss {
    /// The name lookup or TCP connect failed.
    #[error("cannot reach the relay: {0}")]
    Connect(String),
    /// The TLS handshake failed.
    #[error("{0}")]
    Tls(String),
    /// The WebSocket handshake failed (`Status(401)`: the token was refused).
    #[error("the relay refused the WebSocket upgrade: {0}")]
    Handshake(HandshakeError),
    /// The relay refused the connection.
    #[error("the relay refused the connection: {0:?}")]
    Refused(RefuseReason),
    /// The relay speaks another protocol version.
    #[error("the relay speaks protocol {0}")]
    ProtocolVersion(u32),
    /// No `hello_ok` in time.
    #[error("the relay did not answer the hello in time")]
    HelloTimeout,
    /// No frame from the relay for the silence time.
    #[error("the relay fell silent")]
    Silent,
    /// The WebSocket failed: the stream, or a frame the relay sent.
    #[error("{0}")]
    Ws(WsError),
    /// A control message could not be read.
    #[error("{0}")]
    Message(MessageError),
    /// The relay sent something out of order.
    #[error("the relay sent {0}")]
    Unexpected(&'static str),
    /// The relay closed the WebSocket, with its close code if it gave one.
    #[error("the relay closed the connection (code {0:?})")]
    Closed(Option<u16>),
    /// The caller left more than [`LinkConfig::inbound_bytes`] in events it
    /// had not taken.
    #[error("the events waiting to be read hold too many bytes")]
    InboundOverflow,
}

/// What happened on the link, in order.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Inbound {
    /// The relay accepted the hello; messages may be sent now.
    Connected {
        /// How often the relay pings, in milliseconds.
        ping_interval_ms: u64,
    },
    /// A control message (never `hello_ok` or `refused`: the link handles
    /// those). [`RelayMessage::Unknown`] may be ignored.
    Control(RelayMessage),
    /// A binary frame.
    Binary(Vec<u8>),
    /// A connected link was lost; whatever was queued is dropped. The link
    /// reconnects after `retry_in`.
    Disconnected {
        /// Why.
        loss: LinkLoss,
        /// The wait before the next attempt.
        retry_in: Duration,
    },
    /// An attempt failed before `hello_ok`. The link retries after
    /// `retry_in`.
    ConnectFailed {
        /// Why.
        loss: LinkLoss,
        /// The wait before the next attempt.
        retry_in: Duration,
    },
}

/// What an event that carries no payload counts against the inbound
/// budget.
const EVENT_OVERHEAD: usize = 64;

/// The link's events, in order. Taking one frees its bytes from the inbound
/// budget ([`LinkConfig::inbound_bytes`]); the methods are those of
/// [`std::sync::mpsc::Receiver`].
pub struct Events {
    receiver: Receiver<(usize, Inbound)>,
    held: Arc<AtomicUsize>,
}

impl fmt::Debug for Events {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Events")
            .field("held_bytes", &self.held.load(Ordering::SeqCst))
            .finish_non_exhaustive()
    }
}

impl Events {
    fn taken(&self, (cost, event): (usize, Inbound)) -> Inbound {
        self.held.fetch_sub(cost, Ordering::SeqCst);
        event
    }

    /// The next event, waiting for it.
    ///
    /// # Errors
    /// The worker is gone.
    pub fn recv(&self) -> Result<Inbound, RecvError> {
        self.receiver.recv().map(|e| self.taken(e))
    }

    /// The next event, waiting at most `wait`.
    ///
    /// # Errors
    /// None arrived in time, or the worker is gone.
    pub fn recv_timeout(&self, wait: Duration) -> Result<Inbound, RecvTimeoutError> {
        self.receiver.recv_timeout(wait).map(|e| self.taken(e))
    }

    /// The next event if there is one.
    ///
    /// # Errors
    /// None is waiting, or the worker is gone.
    pub fn try_recv(&self) -> Result<Inbound, TryRecvError> {
        self.receiver.try_recv().map(|e| self.taken(e))
    }

    /// The bytes held in events not taken yet.
    #[must_use]
    pub fn held_bytes(&self) -> usize {
        self.held.load(Ordering::SeqCst)
    }
}

/// The worker's end of the events: counts what it hands over.
struct Outbox {
    sender: Sender<(usize, Inbound)>,
    held: Arc<AtomicUsize>,
    budget: usize,
}

impl Outbox {
    /// Hands over an event that is the link's own news (connected, lost);
    /// never refused, as at most one arrives per connection attempt.
    fn news(&self, event: Inbound) -> bool {
        self.held.fetch_add(EVENT_OVERHEAD, Ordering::SeqCst);
        self.sender.send((EVENT_OVERHEAD, event)).is_ok()
    }

    /// Hands over something the relay sent, counted as `bytes`.
    fn relayed(&self, event: Inbound, bytes: usize) -> Result<(), LinkLoss> {
        let cost = bytes.saturating_add(EVENT_OVERHEAD);
        let before = self.held.fetch_add(cost, Ordering::SeqCst);
        if before.saturating_add(cost) > self.budget {
            // A caller that stopped taking events: hold nothing more for it.
            self.held.fetch_sub(cost, Ordering::SeqCst);
            return Err(LinkLoss::InboundOverflow);
        }
        if self.sender.send((cost, event)).is_err() {
            // The link is being dropped; nobody will take it.
            self.held.fetch_sub(cost, Ordering::SeqCst);
        }
        Ok(())
    }
}

/// Why a message was not queued.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum SendError {
    /// The link is not connected (no `Connected` yet, or it was lost).
    #[error("the link is not connected")]
    NotConnected,
    /// The outbound queue is full; detach and attach again.
    #[error("the outbound queue is full")]
    QueueFull,
    /// Longer than the relay accepts in one frame.
    #[error("the message is longer than the relay accepts")]
    TooLarge,
    /// The hello is the link's own: change it with [`Link::update_hello`].
    #[error("the link sends the hello itself")]
    HelloIsTheLinks,
}

/// One queued message.
#[derive(Debug, PartialEq, Eq)]
enum Outgoing {
    Text(String),
    Binary(Vec<u8>),
}

impl Outgoing {
    fn len(&self) -> usize {
        match self {
            Self::Text(text) => text.len(),
            Self::Binary(bytes) => bytes.len(),
        }
    }
}

/// The outbound queue, bounded in bytes for binary frames. Control messages
/// are never refused: they are small, and losing one (an `attached` or a
/// `detached`) would leave the relay wrong about the session.
#[derive(Debug)]
struct OutQueue {
    items: VecDeque<Outgoing>,
    bytes: usize,
    limit: usize,
}

impl OutQueue {
    fn new(limit: usize) -> Self {
        Self {
            items: VecDeque::new(),
            bytes: 0,
            limit,
        }
    }

    fn push_control(&mut self, text: String) {
        self.bytes += text.len();
        self.items.push_back(Outgoing::Text(text));
    }

    fn push_binary(&mut self, bytes: Vec<u8>) -> Result<(), SendError> {
        if self.bytes.saturating_add(bytes.len()) > self.limit {
            return Err(SendError::QueueFull);
        }
        self.bytes += bytes.len();
        self.items.push_back(Outgoing::Binary(bytes));
        Ok(())
    }

    fn pop(&mut self) -> Option<Outgoing> {
        let item = self.items.pop_front()?;
        self.bytes -= item.len();
        Some(item)
    }

    fn clear(&mut self) {
        self.items.clear();
        self.bytes = 0;
    }
}

/// What the caller and the worker share.
#[derive(Debug)]
struct State {
    /// The session's state as the next hello tells it.
    hello: Hello,
    /// Between `hello_ok` and the loss of the link.
    connected: bool,
    queue: OutQueue,
}

#[derive(Debug)]
struct Shared {
    state: Mutex<State>,
    /// Wakes the worker: something was queued, or it should stop.
    wake: Condvar,
    stop: Arc<AtomicBool>,
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn stopping(&self) -> bool {
        self.stop.load(Ordering::SeqCst)
    }
}

/// The link to the relay; dropping it stops the worker and closes the
/// connection.
pub struct Link {
    shared: Arc<Shared>,
    events: Events,
    worker: Option<JoinHandle<()>>,
    limits: WsLimits,
}

impl fmt::Debug for Link {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Link")
            .field("connected", &self.shared.lock().connected)
            .finish_non_exhaustive()
    }
}

impl Link {
    /// Starts the worker; it connects at once.
    ///
    /// # Errors
    /// The thread could not be started.
    pub fn start(config: LinkConfig) -> std::io::Result<Self> {
        let shared = Arc::new(Shared {
            state: Mutex::new(State {
                hello: config.hello.clone(),
                connected: false,
                queue: OutQueue::new(config.queue_bytes),
            }),
            wake: Condvar::new(),
            stop: Arc::new(AtomicBool::new(false)),
        });
        let (sender, receiver) = mpsc::channel();
        let held = Arc::new(AtomicUsize::new(0));
        let events = Events {
            receiver,
            held: Arc::clone(&held),
        };
        let outbox = Outbox {
            sender,
            held,
            budget: config.inbound_bytes,
        };
        let limits = config.limits;
        let worker_shared = Arc::clone(&shared);
        let worker = std::thread::Builder::new()
            .name("remote-link".to_owned())
            .spawn(move || run(&worker_shared, &config, &outbox))?;
        Ok(Self {
            shared,
            events,
            worker: Some(worker),
            limits,
        })
    }

    /// The events, in order.
    #[must_use]
    pub fn events(&self) -> &Events {
        &self.events
    }

    /// Queues a control message. A [`EndpointMessage::Status`] updates the
    /// hello (see [`Link::update_hello`]) and is never refused.
    ///
    /// # Errors
    /// Not connected, the message is a hello, or it is longer than one text
    /// frame may be.
    pub fn send_control(&self, message: EndpointMessage) -> Result<(), SendError> {
        match message {
            EndpointMessage::Hello(_) => Err(SendError::HelloIsTheLinks),
            EndpointMessage::Status(status) => {
                self.update_hello(|hello| status.apply_to(hello));
                Ok(())
            }
            other => {
                let text = other.to_json();
                if text.len() > self.limits.max_text_out {
                    return Err(SendError::TooLarge);
                }
                let mut state = self.shared.lock();
                if !state.connected {
                    return Err(SendError::NotConnected);
                }
                state.queue.push_control(text);
                drop(state);
                self.shared.wake.notify_all();
                Ok(())
            }
        }
    }

    /// Queues a binary frame.
    ///
    /// # Errors
    /// Not connected, the queue is full, or the frame is longer than the
    /// relay accepts.
    pub fn send_binary(&self, bytes: Vec<u8>) -> Result<(), SendError> {
        if bytes.len() > self.limits.max_binary_out {
            return Err(SendError::TooLarge);
        }
        let mut state = self.shared.lock();
        if !state.connected {
            return Err(SendError::NotConnected);
        }
        state.queue.push_binary(bytes)?;
        drop(state);
        self.shared.wake.notify_all();
        Ok(())
    }

    /// Changes the session's state: the next hello carries it, and while
    /// connected a `status` with the changed members is queued. `change`
    /// runs under the link's lock, so it must not call the link.
    pub fn update_hello(&self, change: impl FnOnce(&mut Hello)) {
        let mut state = self.shared.lock();
        let old = state.hello.clone();
        change(&mut state.hello);
        let status = Status::between(&old, &state.hello);
        if state.connected && !status.is_empty() {
            state
                .queue
                .push_control(EndpointMessage::Status(status).to_json());
            drop(state);
            self.shared.wake.notify_all();
        }
    }

    /// The session's state as the next hello tells it.
    #[must_use]
    pub fn hello(&self) -> Hello {
        self.shared.lock().hello.clone()
    }

    /// Stops the worker and closes the connection; returns once the worker
    /// is gone.
    pub fn shutdown(mut self) {
        self.stop_worker();
    }

    fn stop_worker(&mut self) {
        {
            let _state = self.shared.lock();
            self.shared.stop.store(true, Ordering::SeqCst);
        }
        self.shared.wake.notify_all();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

impl Drop for Link {
    fn drop(&mut self) {
        self.stop_worker();
    }
}

/// The wait before each reconnect.
#[derive(Debug)]
struct Backoff {
    first: Duration,
    max: Duration,
    next: Duration,
}

impl Backoff {
    fn new(timing: &Timing) -> Self {
        let first = timing.backoff_first.min(timing.backoff_max);
        Self {
            first,
            max: timing.backoff_max,
            next: first,
        }
    }

    /// The wait now; the one after it doubles, up to the cap.
    fn next(&mut self) -> Duration {
        let now = self.next;
        self.next = self.next.checked_mul(2).unwrap_or(self.max).min(self.max);
        now
    }

    /// A good connection: the next wait is the first again.
    fn reset(&mut self) {
        self.next = self.first;
    }

    /// Another connection replaced this one: the next wait is the longest.
    fn penalize(&mut self) {
        self.next = self.max;
    }
}

/// How one connection ended.
enum End {
    /// The link was told to stop.
    Stopped,
    /// Lost; `connected` when it had got as far as `hello_ok`.
    Lost { loss: LinkLoss, connected: bool },
}

fn failed(loss: LinkLoss) -> End {
    End::Lost {
        loss,
        connected: false,
    }
}

/// How long the worker waits for a busy socket to take a goodbye.
const GOODBYE_WRITE: Duration = Duration::from_millis(100);

/// The longest the worker naps when nothing is happening.
const LONGEST_NAP: Duration = Duration::from_millis(10);

/// How many events one turn reads before it looks at the queue.
const READ_BURST: usize = 64;

/// How many bytes one turn writes before it reads again.
const WRITE_BURST: usize = 1024 * 1024;

/// The worker: connect, run the connection, wait, again; until told to stop.
fn run(shared: &Shared, config: &LinkConfig, events: &Outbox) {
    let mut backoff = Backoff::new(&config.timing);
    while !shared.stopping() {
        let (loss, connected) = match connect(shared, config, events, &mut backoff) {
            End::Stopped => return,
            End::Lost { loss, connected } => (loss, connected),
        };
        // Nothing queued for this connection goes to the next one, and
        // sending is refused before the caller hears of the loss.
        {
            let mut state = shared.lock();
            state.connected = false;
            state.queue.clear();
        }
        if loss == LinkLoss::Refused(RefuseReason::Duplicate) {
            backoff.penalize();
        }
        let retry_in = backoff.next();
        let event = if connected {
            Inbound::Disconnected { loss, retry_in }
        } else {
            Inbound::ConnectFailed { loss, retry_in }
        };
        if !events.news(event) || wait_or_stop(shared, retry_in) {
            return;
        }
    }
}

/// Waits `wait`; true when told to stop meanwhile.
fn wait_or_stop(shared: &Shared, wait: Duration) -> bool {
    let deadline = net::after(wait);
    let mut state = shared.lock();
    loop {
        if shared.stopping() {
            return true;
        }
        let Some(left) = deadline
            .checked_duration_since(Instant::now())
            .filter(|left| !left.is_zero())
        else {
            return false;
        };
        state = match shared.wake.wait_timeout(state, left) {
            Ok((state, _)) => state,
            Err(poisoned) => poisoned.into_inner().0,
        };
    }
}

/// One connection: TCP, TLS, the WebSocket handshake, then the session.
fn connect(shared: &Shared, config: &LinkConfig, events: &Outbox, backoff: &mut Backoff) -> End {
    let url = &config.url;
    let deadline = net::after(config.timing.connect_timeout);
    let answered = match net::dial(&url.host, url.port, config.timing.connect_timeout) {
        Ok(answered) => answered,
        Err(e) => return failed(LinkLoss::Connect(format!("no connect thread: {e}"))),
    };
    let socket = loop {
        if shared.stopping() {
            return End::Stopped;
        }
        match answered.recv_timeout(LONGEST_NAP) {
            Ok(Ok(socket)) => break socket,
            Ok(Err(text)) => return failed(LinkLoss::Connect(text)),
            Err(mpsc::RecvTimeoutError::Timeout) if Instant::now() < deadline => {}
            Err(mpsc::RecvTimeoutError::Timeout) => {
                return failed(LinkLoss::Connect(format!("{}: timed out", url.host)));
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                return failed(LinkLoss::Connect("the connect thread ended".to_owned()));
            }
        }
    };
    let polling = match Polling::new(socket, deadline, Arc::clone(&shared.stop)) {
        Ok(polling) => polling,
        Err(e) => return failed(LinkLoss::Connect(e.kind().to_string())),
    };
    let transport = if url.secure {
        match net::secure(polling, &url.host) {
            Ok(transport) => transport,
            Err(_) if shared.stopping() => return End::Stopped,
            Err(text) => return failed(LinkLoss::Tls(text)),
        }
    } else {
        Transport::Plain(polling)
    };
    let host = url.host_header();
    let request = Handshake {
        host: &host,
        path: &url.path,
        bearer: Some(&config.token),
    };
    let mut ws = match WsClient::handshake(transport, &request, config.limits, deadline) {
        Ok(ws) => ws,
        Err(_) if shared.stopping() => return End::Stopped,
        Err(e) => return failed(LinkLoss::Handshake(e)),
    };
    ws.get_mut().stop_waiting();
    let end = Session {
        shared,
        timing: &config.timing,
        events,
        backoff,
        ws: &mut ws,
        connected: false,
        welcomed_at: None,
        sent_hello: None,
    }
    .run();
    say_goodbye(&mut ws, &end);
    end
}

/// Closes the WebSocket with a code that says why, without waiting long,
/// then the socket.
fn say_goodbye(ws: &mut WsClient<Transport>, end: &End) {
    let code = match end {
        End::Stopped => Some(1001),
        End::Lost { loss, .. } => match loss {
            LinkLoss::Ws(WsError::TooBig { .. }) => Some(1009),
            LinkLoss::Ws(WsError::InvalidUtf8) => Some(1007),
            LinkLoss::Ws(WsError::Io(_) | WsError::Closed | WsError::AlreadyClosed)
            | LinkLoss::Closed(_) => None,
            LinkLoss::Ws(_)
            | LinkLoss::Message(_)
            | LinkLoss::Unexpected(_)
            | LinkLoss::ProtocolVersion(_) => Some(1002),
            LinkLoss::HelloTimeout | LinkLoss::Silent => Some(1001),
            // The endpoint's own policy: it will not hold more for its caller.
            LinkLoss::InboundOverflow => Some(1008),
            _ => Some(1000),
        },
    };
    ws.get_mut().set_write_deadline(net::after(GOODBYE_WRITE));
    if let Some(code) = code {
        let _ = ws.close(code, "");
    }
    ws.get_mut().shutdown();
}

/// A connection after the WebSocket handshake.
struct Session<'a> {
    shared: &'a Shared,
    timing: &'a Timing,
    events: &'a Outbox,
    backoff: &'a mut Backoff,
    ws: &'a mut WsClient<Transport>,
    /// Since `hello_ok`.
    connected: bool,
    /// When `hello_ok` came; the backoff starts over once the connection
    /// has stayed up `stable_after` since.
    welcomed_at: Option<Instant>,
    /// The hello as sent, to tell the relay what changed before `hello_ok`.
    sent_hello: Option<Hello>,
}

impl Session<'_> {
    fn run(mut self) -> End {
        let hello = self.shared.lock().hello.clone();
        if let Err(e) = self
            .ws
            .send_text(&EndpointMessage::Hello(hello.clone()).to_json())
        {
            return failed(LinkLoss::Ws(e));
        }
        self.sent_hello = Some(hello);
        let hello_sent = Instant::now();
        let mut last_frame = hello_sent;
        let mut nap = Duration::from_millis(1);
        loop {
            if self.shared.stopping() {
                return End::Stopped;
            }
            let mut busy = false;
            // Pongs go out while reading; a relay that stops reading is as
            // lost as a silent one.
            self.ws
                .get_mut()
                .set_write_deadline(net::after(self.timing.silence));
            for _ in 0..READ_BURST {
                match self.ws.read_event() {
                    Ok(None) => break,
                    Ok(Some(event)) => {
                        busy = true;
                        last_frame = Instant::now();
                        if let Err(loss) = self.handle(event) {
                            return self.lost(loss);
                        }
                    }
                    Err(e) => return self.lost(LinkLoss::Ws(e)),
                }
            }
            let now = Instant::now();
            if !self.connected && now.duration_since(hello_sent) >= self.timing.hello_timeout {
                return self.lost(LinkLoss::HelloTimeout);
            }
            if now.duration_since(last_frame) >= self.timing.silence {
                return self.lost(LinkLoss::Silent);
            }
            if self
                .welcomed_at
                .is_some_and(|at| now.duration_since(at) >= self.timing.stable_after)
            {
                self.backoff.reset();
                self.welcomed_at = None;
            }
            if self.connected {
                let mut written = 0;
                while written < WRITE_BURST {
                    let Some(item) = self.shared.lock().queue.pop() else {
                        break;
                    };
                    busy = true;
                    written += item.len();
                    self.ws
                        .get_mut()
                        .set_write_deadline(net::after(self.timing.silence));
                    let sent = match &item {
                        Outgoing::Text(text) => self.ws.send_text(text),
                        Outgoing::Binary(bytes) => self.ws.send_binary(bytes),
                    };
                    if let Err(e) = sent {
                        return self.lost(LinkLoss::Ws(e));
                    }
                }
            }
            if busy {
                nap = Duration::from_millis(1);
            } else {
                let state = self.shared.lock();
                if state.queue.items.is_empty() && !self.shared.stopping() {
                    let _ = self.shared.wake.wait_timeout(state, nap);
                }
                nap = (nap * 2).min(LONGEST_NAP);
            }
        }
    }

    fn lost(&self, loss: LinkLoss) -> End {
        End::Lost {
            loss,
            connected: self.connected,
        }
    }

    fn handle(&mut self, event: WsEvent) -> Result<(), LinkLoss> {
        match event {
            WsEvent::Ping | WsEvent::Pong => Ok(()),
            WsEvent::Close(given) => Err(LinkLoss::Closed(given.map(|(code, _)| code))),
            WsEvent::Binary(bytes) => {
                if !self.connected {
                    return Err(LinkLoss::Unexpected("a binary frame before hello_ok"));
                }
                let size = bytes.len();
                self.events.relayed(Inbound::Binary(bytes), size)
            }
            WsEvent::Text(text) => {
                let size = text.len();
                let message = RelayMessage::parse(&text).map_err(LinkLoss::Message)?;
                match message {
                    RelayMessage::HelloOk {
                        protocol,
                        ping_interval_ms,
                    } => {
                        if self.connected {
                            return Ok(());
                        }
                        if protocol != PROTOCOL_VERSION {
                            return Err(LinkLoss::ProtocolVersion(protocol));
                        }
                        self.welcomed(ping_interval_ms);
                        Ok(())
                    }
                    RelayMessage::Refused { reason } => Err(LinkLoss::Refused(reason)),
                    RelayMessage::Unknown { .. } if !self.connected => Ok(()),
                    _ if !self.connected => {
                        Err(LinkLoss::Unexpected("a control message before hello_ok"))
                    }
                    other => self.events.relayed(Inbound::Control(other), size),
                }
            }
        }
    }

    /// `hello_ok`: sending opens, and whatever changed since the hello was
    /// sent follows as a status.
    fn welcomed(&mut self, ping_interval_ms: u64) {
        {
            let mut state = self.shared.lock();
            state.connected = true;
            if let Some(sent) = &self.sent_hello {
                let status = Status::between(sent, &state.hello);
                if !status.is_empty() {
                    state
                        .queue
                        .push_control(EndpointMessage::Status(status).to_json());
                }
            }
        }
        self.connected = true;
        self.welcomed_at = Some(Instant::now());
        self.events.news(Inbound::Connected { ping_interval_ms });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_queue_bounds_binary_bytes_but_never_control() {
        let mut queue = OutQueue::new(1000);
        queue.push_binary(vec![0; 600]).unwrap();
        assert_eq!(queue.push_binary(vec![0; 401]), Err(SendError::QueueFull));
        queue.push_binary(vec![0; 400]).unwrap();
        assert_eq!(queue.push_binary(vec![0; 1]), Err(SendError::QueueFull));
        // Control goes in even when full.
        queue.push_control("x".repeat(50));
        assert_eq!(queue.bytes, 1050);
        assert_eq!(queue.pop(), Some(Outgoing::Binary(vec![0; 600])));
        assert_eq!(queue.bytes, 450);
        queue.push_binary(vec![1; 550]).unwrap();
        assert_eq!(queue.pop(), Some(Outgoing::Binary(vec![0; 400])));
        assert_eq!(queue.pop(), Some(Outgoing::Text("x".repeat(50))));
        assert_eq!(queue.pop(), Some(Outgoing::Binary(vec![1; 550])));
        assert_eq!(queue.pop(), None);
        assert_eq!(queue.bytes, 0);
        queue.push_binary(vec![0; 1000]).unwrap();
        queue.clear();
        assert_eq!((queue.bytes, queue.pop()), (0, None));
    }

    #[test]
    fn backoff_doubles_to_the_cap_and_starts_over() {
        let timing = Timing {
            backoff_first: Duration::from_secs(1),
            backoff_max: Duration::from_secs(60),
            ..Timing::default()
        };
        let mut backoff = Backoff::new(&timing);
        let waits: Vec<u64> = (0..8).map(|_| backoff.next().as_secs()).collect();
        assert_eq!(waits, [1, 2, 4, 8, 16, 32, 60, 60]);
        backoff.reset();
        assert_eq!(backoff.next(), Duration::from_secs(1));
        // Replaced by a newer connection: the longest wait, then it stays
        // there until a good connection.
        backoff.penalize();
        assert_eq!(backoff.next(), Duration::from_secs(60));
        assert_eq!(backoff.next(), Duration::from_secs(60));
    }

    #[test]
    fn events_free_their_bytes_when_taken() {
        let (sender, receiver) = mpsc::channel();
        let held = Arc::new(AtomicUsize::new(0));
        let events = Events {
            receiver,
            held: Arc::clone(&held),
        };
        let outbox = Outbox {
            sender,
            held,
            budget: 1000,
        };
        outbox.relayed(Inbound::Binary(vec![0; 400]), 400).unwrap();
        outbox.relayed(Inbound::Binary(vec![0; 400]), 400).unwrap();
        // Past the budget: refused, and nothing more is held.
        assert_eq!(
            outbox.relayed(Inbound::Binary(vec![0; 400]), 400),
            Err(LinkLoss::InboundOverflow)
        );
        assert_eq!(events.held_bytes(), 2 * (400 + EVENT_OVERHEAD));
        // The link's own news always goes through.
        assert!(outbox.news(Inbound::Connected {
            ping_interval_ms: 1
        }));
        assert!(events.try_recv().is_ok());
        assert!(events.recv_timeout(Duration::from_millis(1)).is_ok());
        assert!(events.recv().is_ok());
        assert_eq!(events.held_bytes(), 0);
        // Taking frees room again.
        outbox.relayed(Inbound::Binary(vec![0; 400]), 400).unwrap();
    }
}
