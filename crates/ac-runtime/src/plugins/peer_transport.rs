//! This client's line to the peer hub (OpenAC `LocalPeerTransport`). It
//! does nothing until a plugin subscribes: the subscriptions of all the
//! session's plugins are combined, and while any is held (and the character
//! is in the world) a worker thread keeps a connection to the hub, starting
//! the hub when none answers and trying again with a growing pause (1 s to
//! 30 s). The session's own thread owns the state; the worker only moves
//! bytes, handing what arrives to the session's next tick.
//!
//! What the hub pushes lands in the session's peer registry (other clients,
//! their casts); a broadcast line aimed here is returned to the session to
//! run after its stagger. A queue that overflows closes the connection so
//! the gap shows, and nothing is replayed after a connection drops.

use std::collections::{HashMap, HashSet, VecDeque};
use std::io;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use ac_plugin::abi::peers::{NetworkClient, PeerCast};
use ac_plugin_net::local::{LocalEndpoint, LocalStream};

use super::peer_wire::{
    self, ALL_CAPABILITIES, CASTS, CLIENT_STATE, COMMANDS, PeerOutbox, SEND_COMMANDS, WireMessage,
};
use super::peers::{InstanceId, PeerRegistry};

/// How long a broadcast waits for the hub's acknowledgement, milliseconds.
const ACKNOWLEDGEMENT_MS: i64 = 15_000;
/// How many line ids are remembered to drop a repeat.
const REMEMBERED_COMMANDS: usize = 256;
/// The longest pause between connection attempts, seconds.
const LONGEST_BACKOFF_SECONDS: u64 = 30;

/// Starts the hub process.
pub type StartHub = Arc<dyn Fn() -> io::Result<()> + Send + Sync>;

/// What the worker tells the session.
enum WorkerEvent {
    Connected(Arc<PeerOutbox>),
    Message(Arc<PeerOutbox>, Box<WireMessage>),
    Disconnected(Arc<PeerOutbox>),
    Report(String),
}

/// The running connection loop.
struct Worker {
    stop: Arc<AtomicBool>,
    stream: Arc<Mutex<Option<LocalStream>>>,
    events: Receiver<WorkerEvent>,
}

impl Worker {
    fn cancel(&self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(stream) = self
            .stream
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .as_ref()
        {
            stream.shutdown();
        }
    }
}

/// A line the hub delivered, for the session to run.
#[derive(Debug, Clone, PartialEq)]
pub struct PushedCommand {
    /// Who sent it.
    pub sender: NetworkClient,
    /// The line.
    pub line: String,
    /// The labels it was aimed at.
    pub tags: Vec<String>,
    /// This client's wait, milliseconds.
    pub delay_ms: i32,
    /// When it was sent, UTC milliseconds.
    pub sent_unix_ms: i64,
}

/// The session's side of the peer hub.
pub struct PeerTransport {
    endpoint: LocalEndpoint,
    instance: InstanceId,
    start_hub: StartHub,
    leases: HashMap<u64, u32>,
    next_lease: u64,
    capabilities: u32,
    demand: u32,
    worker: Option<Worker>,
    outbox: Option<Arc<PeerOutbox>>,
    me: Option<NetworkClient>,
    ready: bool,
    disposed: bool,
    received: HashSet<InstanceId>,
    received_order: VecDeque<InstanceId>,
    /// Broadcasts waiting for their acknowledgement: their send lease and
    /// when they are given up.
    transient: HashMap<InstanceId, (u64, i64)>,
    /// Broadcasts made before the connection was up.
    initial: Vec<WireMessage>,
    next_command: u64,
    reports: Vec<String>,
}

impl std::fmt::Debug for PeerTransport {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PeerTransport")
            .field("endpoint", &self.endpoint)
            .field("capabilities", &self.capabilities)
            .field("demand", &self.demand)
            .field("ready", &self.ready)
            .finish_non_exhaustive()
    }
}

/// The hub's endpoint for a peer folder (OpenAC
/// `PeerHubEndpoint.ForDirectory`): the folder itself, and a pipe name made
/// from it and the user's name, so two users or two installs never meet.
#[must_use]
pub fn endpoint_for(directory: &std::path::Path) -> LocalEndpoint {
    use sha2::{Digest, Sha256};
    let full = std::path::absolute(directory).unwrap_or_else(|_| directory.to_path_buf());
    let user = std::env::var("USERNAME")
        .or_else(|_| std::env::var("USER"))
        .unwrap_or_default();
    let digest = Sha256::digest(format!("{}{user}", full.display()).as_bytes());
    let hash: String = digest.iter().take(12).map(|b| format!("{b:02X}")).collect();
    LocalEndpoint {
        directory: full,
        name: format!("openac-peer-{hash}"),
    }
}

/// Starts the installed client as the hub (`openac peer-hub <folder>`),
/// its own process with no window, which ends once nobody uses it.
#[must_use]
pub fn start_installed_hub(directory: std::path::PathBuf) -> StartHub {
    Arc::new(move || {
        let name = if cfg!(windows) {
            "openac.exe"
        } else {
            "openac"
        };
        let executable = std::env::current_exe()?
            .parent()
            .map(|folder| folder.join(name))
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "no executable folder"))?;
        if !executable.is_file() {
            return Err(io::Error::new(
                io::ErrorKind::NotFound,
                format!(
                    "The local peer hub requires the installed client: {}",
                    executable.display()
                ),
            ));
        }
        let mut command = std::process::Command::new(executable);
        command
            .arg("peer-hub")
            .arg(&directory)
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            // CREATE_NO_WINDOW: no console flashes up.
            command.creation_flags(0x0800_0000);
        }
        let mut child = command.spawn()?;
        // The hub's exit is collected, so it leaves no defunct process
        // behind on Unix.
        let _ = std::thread::Builder::new()
            .name("peer-hub-wait".into())
            .spawn(move || {
                let _ = child.wait();
            });
        Ok(())
    })
}

/// The connection loop (OpenAC `RunAsync`), on its own thread.
fn run_worker(
    endpoint: &LocalEndpoint,
    start_hub: &StartHub,
    stop: &AtomicBool,
    current: &Mutex<Option<LocalStream>>,
    events: &Sender<WorkerEvent>,
) {
    let mut backoff = 1;
    let mut attempted_start = false;
    while !stop.load(Ordering::SeqCst) {
        match endpoint.connect(Duration::from_secs(1)) {
            Ok(stream) => {
                *current.lock().unwrap_or_else(PoisonError::into_inner) = Some(stream.clone());
                if stop.load(Ordering::SeqCst) {
                    stream.shutdown();
                    break;
                }
                let outbox = Arc::new(PeerOutbox::default());
                if events
                    .send(WorkerEvent::Connected(Arc::clone(&outbox)))
                    .is_err()
                {
                    stream.shutdown();
                    break;
                }
                backoff = 1;
                attempted_start = false;
                let writer_stream = stream.clone();
                let writer_outbox = Arc::clone(&outbox);
                let writer_events = events.clone();
                let writer = std::thread::Builder::new()
                    .name("peer-writer".into())
                    .spawn(move || {
                        while !writer_outbox.is_disposed() {
                            let Some(message) = writer_outbox.next(Duration::from_millis(250))
                            else {
                                continue;
                            };
                            if let Err(error) = peer_wire::write(&writer_stream, &message) {
                                let _ = writer_events.send(WorkerEvent::Report(format!(
                                    "Peer writer disconnected: {error}"
                                )));
                                break;
                            }
                        }
                        writer_stream.shutdown();
                    });
                loop {
                    match peer_wire::read(&stream) {
                        Ok(Some(message)) => {
                            if events
                                .send(WorkerEvent::Message(Arc::clone(&outbox), Box::new(message)))
                                .is_err()
                            {
                                break;
                            }
                        }
                        Ok(None) => break,
                        Err(error) => {
                            if !stop.load(Ordering::SeqCst) && !outbox.is_disposed() {
                                let _ = events.send(WorkerEvent::Report(format!(
                                    "Peer transport disconnected: {error}"
                                )));
                            }
                            break;
                        }
                    }
                }
                outbox.dispose();
                stream.shutdown();
                if let Ok(writer) = writer {
                    let _ = writer.join();
                }
                *current.lock().unwrap_or_else(PoisonError::into_inner) = None;
                let _ = events.send(WorkerEvent::Disconnected(outbox));
            }
            Err(error) => {
                if stop.load(Ordering::SeqCst) {
                    break;
                }
                if attempted_start {
                    let _ = events.send(WorkerEvent::Report(format!(
                        "Peer transport unavailable: {error}"
                    )));
                } else {
                    attempted_start = true;
                    if let Err(start) = start_hub() {
                        let _ = events.send(WorkerEvent::Report(format!(
                            "Peer hub startup failed: {start}"
                        )));
                    }
                }
            }
        }
        // The pause, watching for the stop.
        for _ in 0..backoff * 20 {
            if stop.load(Ordering::SeqCst) {
                return;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        backoff = (backoff * 2).min(LONGEST_BACKOFF_SECONDS);
    }
}

impl PeerTransport {
    /// A transport to `endpoint` for this client's `instance`, starting the
    /// hub with `start_hub` when none answers.
    #[must_use]
    pub fn new(endpoint: LocalEndpoint, instance: InstanceId, start_hub: StartHub) -> Self {
        Self {
            endpoint,
            instance,
            start_hub,
            leases: HashMap::new(),
            next_lease: 0,
            capabilities: 0,
            demand: 0,
            worker: None,
            outbox: None,
            me: None,
            ready: false,
            disposed: false,
            received: HashSet::new(),
            received_order: VecDeque::new(),
            transient: HashMap::new(),
            initial: Vec::new(),
            next_command: 0,
            reports: Vec::new(),
        }
    }

    /// Registered with the hub and connected (OpenAC `IsConnected`).
    #[must_use]
    pub fn is_connected(&self) -> bool {
        !self.disposed && self.ready && self.outbox.is_some()
    }

    /// Some plugin takes part in something (OpenAC `IsActive`).
    #[must_use]
    pub fn is_active(&self) -> bool {
        !self.disposed && self.capabilities != 0
    }

    /// A plugin takes part in `capability`.
    #[must_use]
    pub fn has(&self, capability: u32) -> bool {
        self.capabilities & capability != 0
    }

    /// This client's own record is known.
    #[must_use]
    pub fn has_self(&self) -> bool {
        self.me.is_some()
    }

    /// What the other clients want this one to publish.
    #[must_use]
    pub fn demand(&self) -> u32 {
        self.demand
    }

    /// Another client wants this one's state.
    #[must_use]
    pub fn needs_state(&self) -> bool {
        self.demand & CLIENT_STATE != 0
    }

    /// Drops the current connection (the worker connects again), saying
    /// why: what the session could not take in is a gap to show.
    pub fn close_connection(&mut self, why: &str) {
        self.reports.push(why.to_owned());
        if let Some(worker) = &self.worker
            && let Some(stream) = worker
                .stream
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .as_ref()
        {
            stream.shutdown();
        }
    }

    /// The lines worth logging since the last call.
    pub fn take_reports(&mut self) -> Vec<String> {
        std::mem::take(&mut self.reports)
    }

    /// Takes part in `capabilities` (OpenAC `Subscribe`): the lease, or
    /// none for no capability or an unknown one.
    pub fn subscribe(&mut self, capabilities: u32, registry: &mut PeerRegistry) -> Option<u64> {
        if capabilities == 0 || capabilities & !ALL_CAPABILITIES != 0 || self.disposed {
            return None;
        }
        self.next_lease += 1;
        let lease = self.next_lease;
        self.leases.insert(lease, capabilities);
        self.update_capabilities(registry);
        Some(lease)
    }

    /// Gives a lease back.
    pub fn release(&mut self, lease: u64, registry: &mut PeerRegistry) {
        if self.leases.remove(&lease).is_some() {
            self.update_capabilities(registry);
        }
    }

    fn stop_worker(&mut self) {
        if let Some(worker) = self.worker.take() {
            worker.cancel();
        }
        if let Some(outbox) = self.outbox.take() {
            outbox.dispose();
        }
    }

    fn update_capabilities(&mut self, registry: &mut PeerRegistry) {
        self.capabilities = self.leases.values().fold(0, |a, b| a | b);
        if self.capabilities == 0 {
            self.stop_worker();
            self.demand = 0;
            registry.clear_pushed_clients();
            return;
        }
        if self.outbox.is_some()
            && let Some(me) = self.me.clone()
        {
            self.queue_register(me);
        }
        if self.worker.is_none() && self.me.is_some() {
            self.start_worker();
        }
    }

    fn start_worker(&mut self) {
        let stop = Arc::new(AtomicBool::new(false));
        let stream = Arc::new(Mutex::new(None));
        let (sender, events) = mpsc::channel();
        let endpoint = self.endpoint.clone();
        let start_hub = Arc::clone(&self.start_hub);
        let worker_stop = Arc::clone(&stop);
        let worker_stream = Arc::clone(&stream);
        let started = std::thread::Builder::new()
            .name("peer-transport".into())
            .spawn(move || {
                run_worker(&endpoint, &start_hub, &worker_stop, &worker_stream, &sender);
            });
        match started {
            Ok(_) => {
                self.worker = Some(Worker {
                    stop,
                    stream,
                    events,
                });
            }
            Err(error) => self
                .reports
                .push(format!("Peer transport stopped: {error}")),
        }
    }

    fn queue_register(&mut self, me: NetworkClient) {
        self.ready = false;
        let message = WireMessage {
            id: self.instance,
            client: me,
            capabilities: self.capabilities,
            ..WireMessage::of("register")
        };
        if !self.outbox.as_ref().is_some_and(|o| o.enqueue(message)) {
            self.fail_queue();
        }
    }

    /// A queue overflowed: the connection closes so the gap shows.
    fn fail_queue(&mut self) {
        self.reports
            .push("Peer event queue is full; connection closed to expose the gap.".to_owned());
        self.stop_worker();
    }

    /// This client's record now (OpenAC `UpdateSelf`): registered again
    /// when who or where it is or its labels changed, else its state sent
    /// when somebody wants it.
    pub fn update_self(&mut self, me: NetworkClient, registry: &mut PeerRegistry) {
        if self.disposed {
            return;
        }
        let identity_changed = self.me.as_ref().is_none_or(|previous| {
            previous.player_id != me.player_id
                || previous.world_name != me.world_name
                || previous.tags.len() != me.tags.len()
                || previous
                    .tags
                    .iter()
                    .zip(&me.tags)
                    .any(|(a, b)| !a.eq_ignore_ascii_case(b))
        });
        let changed = self.me.as_ref().is_none_or(|old| {
            NetworkClient {
                tags: me.tags.clone(),
                ..old.clone()
            } != me
        });
        if let Some(previous) = &self.me
            && (previous.player_id != me.player_id || previous.world_name != me.world_name)
        {
            registry.clear_pushed_clients();
        }
        self.me = Some(me.clone());
        if self.outbox.is_some() && identity_changed {
            self.queue_register(me);
        } else if self.outbox.is_some() && changed && self.needs_state() {
            let message = WireMessage {
                id: self.instance,
                client: me,
                ..WireMessage::of("state")
            };
            if !self.outbox.as_ref().is_some_and(|o| o.enqueue(message)) {
                self.fail_queue();
            }
        }
        if self.worker.is_none() && self.capabilities != 0 {
            self.update_capabilities(registry);
        }
    }

    /// Sends one of this client's casts (OpenAC `SendCast`): false when no
    /// plugin shares casts; true without sending while nobody wants them.
    pub fn send_cast(&mut self, cast: PeerCast) -> bool {
        if !self.has(CASTS) {
            return false;
        }
        if self.demand & CASTS == 0 {
            return true;
        }
        let message = WireMessage {
            id: self.instance,
            cast,
            ..WireMessage::of("cast")
        };
        let queued = self.outbox.as_ref().is_some_and(|o| o.enqueue(message));
        if !queued {
            self.fail_queue();
        }
        queued
    }

    /// Broadcasts a line (OpenAC `SendCommand`): it holds a send lease of
    /// its own until the hub acknowledges it or 15 s pass; made before the
    /// connection is up, it goes once it is.
    pub fn send_command(
        &mut self,
        line: &str,
        tags: Vec<String>,
        delay_ms: i32,
        now_ms: i64,
        registry: &mut PeerRegistry,
    ) -> bool {
        let Some(lease) = self.subscribe(SEND_COMMANDS, registry) else {
            return false;
        };
        self.next_command += 1;
        let id = command_id(self.instance, self.next_command);
        let message = WireMessage {
            id,
            line: line.to_owned(),
            tags,
            delay_ms,
            created_unix_ms: now_ms,
            ..WireMessage::of("command")
        };
        if let Some(outbox) = &self.outbox {
            if !outbox.enqueue(message) {
                self.release(lease, registry);
                self.fail_queue();
                return false;
            }
        } else {
            if self.initial.len() >= 256 {
                self.release(lease, registry);
                return false;
            }
            self.initial.push(message);
        }
        self.transient
            .insert(id, (lease, now_ms + ACKNOWLEDGEMENT_MS));
        true
    }

    /// The character left (OpenAC `Withdraw`): the connection goes, and
    /// what it pushed.
    pub fn withdraw(&mut self, registry: &mut PeerRegistry) {
        self.me = None;
        self.stop_worker();
        self.demand = 0;
        registry.clear_pushed_clients();
    }

    /// The session ends.
    pub fn dispose(&mut self, registry: &mut PeerRegistry) {
        self.disposed = true;
        self.leases.clear();
        self.stop_worker();
        registry.clear_pushed_clients();
    }

    /// Takes in what the worker brought (OpenAC `Receive` and the
    /// connection's start and end) and gives up broadcasts never
    /// acknowledged: the lines aimed here, in order.
    pub fn pump(&mut self, registry: &mut PeerRegistry, now_ms: i64) -> Vec<PushedCommand> {
        let mut delivered = Vec::new();
        let events: Vec<WorkerEvent> = match &self.worker {
            Some(worker) => worker.events.try_iter().collect(),
            None => Vec::new(),
        };
        for event in events {
            match event {
                WorkerEvent::Connected(outbox) => {
                    let Some(me) = self.me.clone() else {
                        outbox.dispose();
                        continue;
                    };
                    self.outbox = Some(outbox);
                    self.queue_register(me);
                    for message in std::mem::take(&mut self.initial) {
                        if !self.outbox.as_ref().is_some_and(|o| o.enqueue(message)) {
                            self.fail_queue();
                        }
                    }
                }
                WorkerEvent::Message(outbox, message) => {
                    if self
                        .outbox
                        .as_ref()
                        .is_some_and(|current| Arc::ptr_eq(current, &outbox))
                        && let Some(command) = self.receive(*message, registry, now_ms)
                    {
                        delivered.push(command);
                    }
                }
                WorkerEvent::Disconnected(outbox) => {
                    if self
                        .outbox
                        .as_ref()
                        .is_some_and(|current| Arc::ptr_eq(current, &outbox))
                    {
                        self.outbox = None;
                        self.demand = 0;
                        registry.clear_pushed_clients();
                    }
                    let leases: Vec<u64> = self.transient.drain().map(|(_, (l, _))| l).collect();
                    for lease in leases {
                        self.release(lease, registry);
                    }
                    self.initial.clear();
                }
                WorkerEvent::Report(text) => self.reports.push(text),
            }
        }
        let expired: Vec<InstanceId> = self
            .transient
            .iter()
            .filter(|(_, (_, until))| *until <= now_ms)
            .map(|(id, _)| *id)
            .collect();
        for id in expired {
            if let Some((lease, _)) = self.transient.remove(&id) {
                self.initial.retain(|m| m.id != id);
                self.release(lease, registry);
                self.reports.push(
                    "Peer broadcast acknowledgement timed out; command was not replayed."
                        .to_owned(),
                );
            }
        }
        delivered
    }

    fn receive(
        &mut self,
        message: WireMessage,
        registry: &mut PeerRegistry,
        now_ms: i64,
    ) -> Option<PushedCommand> {
        if self.disposed {
            return None;
        }
        match message.kind.as_str() {
            "ready" => self.ready = true,
            "demand" => self.demand = message.capabilities,
            "state" => registry.push_client(message.id, &message.client, now_ms),
            "remove" => registry.remove_pushed_client(message.id),
            "cast" => {
                registry.push_client(message.id, &message.client, now_ms);
                registry.push_cast(message.id, &message.cast, message.created_unix_ms);
            }
            "command" => {
                if !self.has(COMMANDS) || !self.received.insert(message.id) {
                    return None;
                }
                self.received_order.push_back(message.id);
                while self.received_order.len() > REMEMBERED_COMMANDS {
                    if let Some(old) = self.received_order.pop_front() {
                        self.received.remove(&old);
                    }
                }
                return Some(PushedCommand {
                    sender: message.client,
                    line: message.line,
                    tags: message.tags,
                    delay_ms: message.delay_ms,
                    sent_unix_ms: message.created_unix_ms,
                });
            }
            "ack" => {
                if let Some((lease, _)) = self.transient.remove(&message.id) {
                    self.release(lease, registry);
                }
                if !message.accepted {
                    self.reports
                        .push("Peer broadcast was refused by at least one recipient.".to_owned());
                }
            }
            _ => {}
        }
        None
    }
}

impl Drop for PeerTransport {
    fn drop(&mut self) {
        self.stop_worker();
    }
}

/// A broadcast's id: unique to this client's instance and its count (OpenAC
/// uses a fresh GUID).
fn command_id(instance: InstanceId, count: u64) -> InstanceId {
    let mut bytes = instance.0;
    for (byte, add) in bytes[8..].iter_mut().zip(count.to_be_bytes()) {
        *byte ^= add;
    }
    // Never the instance itself, never empty.
    bytes[7] ^= 0x5A;
    InstanceId(bytes)
}
