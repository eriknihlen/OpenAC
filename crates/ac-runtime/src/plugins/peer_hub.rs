//! The peer hub (OpenAC `PeerHubServer`): one per user and peer folder,
//! started on demand by the first client that wants peers and gone 15 s
//! after its last client. Clients register who they are and what they take
//! part in; the hub routes within one world only, to the clients that asked
//! for each thing: a client's state to those that need state, its casts to
//! those sharing casts, a broadcast line to those receiving lines and
//! wearing one of its labels, each with its own stagger, and an
//! acknowledgement back to the sender. It tells each client what the others
//! want from it (its demand), so a client publishes nothing nobody reads.
//!
//! [`Hub`] is the routing, with no I/O; [`serve`] runs it on the local
//! endpoint, a reader and a writer thread per connection.

use std::collections::HashMap;
use std::fs::File;
use std::io;
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use ac_plugin::abi::peers::NetworkClient;
use ac_plugin_net::local::{LocalEndpoint, LocalStream};

use super::peer_wire::{
    self, ALL_CAPABILITIES, CASTS, CLIENT_STATE, COMMANDS, PeerOutbox, WireMessage,
};
use super::peers::InstanceId;

/// How many clients one hub serves.
pub const MAXIMUM_CLIENTS: usize = 64;
/// How long the hub stays with no client.
pub const IDLE_STOP: Duration = Duration::from_secs(15);
/// The longest line a broadcast may carry (UTF-16 units, as .NET counts).
const MAXIMUM_LINE: usize = 512;
/// How long a broadcast is good for, milliseconds.
const COMMAND_LIFETIME_MS: i64 = 15_000;

/// A connection, by the hub's own number.
pub type ConnectionId = u64;

struct Connection {
    id: Option<InstanceId>,
    capabilities: u32,
    demand: u32,
    client: NetworkClient,
    outbox: Arc<PeerOutbox>,
    /// Closes the connection when its queue overflows.
    close: Box<dyn Fn() + Send>,
}

/// The hub's routing state.
#[derive(Default)]
pub struct Hub {
    connections: HashMap<ConnectionId, Connection>,
    /// Registered clients, in the order they registered.
    registered: Vec<ConnectionId>,
}

impl std::fmt::Debug for Hub {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Hub")
            .field("connections", &self.connections.len())
            .field("registered", &self.registered.len())
            .finish()
    }
}

fn invalid(text: &str) -> String {
    text.to_owned()
}

fn validate_client(c: &NetworkClient) -> Result<(), String> {
    let p = &c.position;
    let bad = c.client_id == 0
        || c.player_id == 0
        || c.name.trim().is_empty()
        || c.name.encode_utf16().count() > 128
        || c.world_name.encode_utf16().count() > 128
        || c.tags.len() > 128
        || c.tags.iter().any(|t| t.encode_utf16().count() > 64)
        || !p.east_west.is_finite()
        || !p.north_south.is_finite()
        || !p.elevation.is_finite()
        || !c.heading.is_finite();
    if bad {
        Err(invalid("Invalid peer client state."))
    } else {
        Ok(())
    }
}

impl Hub {
    /// A connection arrived, writing through `outbox`; `close` ends it.
    pub fn open(
        &mut self,
        connection: ConnectionId,
        outbox: Arc<PeerOutbox>,
        close: Box<dyn Fn() + Send>,
    ) {
        self.connections.insert(
            connection,
            Connection {
                id: None,
                capabilities: 0,
                demand: 0,
                client: NetworkClient::default(),
                outbox,
                close,
            },
        );
    }

    /// Registered clients.
    #[must_use]
    pub fn client_count(&self) -> usize {
        self.registered.len()
    }

    fn same_world(&self, a: ConnectionId, b: ConnectionId) -> bool {
        let (Some(a), Some(b)) = (self.connections.get(&a), self.connections.get(&b)) else {
            return false;
        };
        a.client.world_name == b.client.world_name && a.client.player_id != b.client.player_id
    }

    fn needs_state(&self, c: ConnectionId) -> bool {
        self.connections
            .get(&c)
            .is_some_and(|c| c.capabilities & (CLIENT_STATE | CASTS) != 0)
    }

    fn participates(&self, c: ConnectionId) -> bool {
        self.connections
            .get(&c)
            .is_some_and(|c| c.capabilities != 0)
    }

    fn capabilities(&self, c: ConnectionId) -> u32 {
        self.connections.get(&c).map_or(0, |c| c.capabilities)
    }

    /// Queues `message` for `target`; a full queue closes that connection
    /// (the gap is shown, not hidden).
    fn send(&self, target: ConnectionId, message: WireMessage) -> bool {
        let Some(connection) = self.connections.get(&target) else {
            return false;
        };
        if connection.outbox.enqueue(message) {
            return true;
        }
        (connection.close)();
        false
    }

    fn send_state(&self, target: ConnectionId, source: ConnectionId, initial: bool) {
        let Some(from) = self.connections.get(&source) else {
            return;
        };
        let message = WireMessage {
            id: from.id.unwrap_or_default(),
            client: from.client.clone(),
            initial,
            ..WireMessage::of("state")
        };
        self.send(target, message);
    }

    /// What each client must publish: what the others in its world take
    /// part in, of what it takes part in itself; sent when it changes.
    fn update_demand(&mut self) {
        let mut changed = Vec::new();
        for &client in &self.registered {
            let mut demand = 0;
            for &other in &self.registered {
                if other != client && self.same_world(client, other) {
                    demand |= self.capabilities(other);
                }
            }
            demand &= self.capabilities(client);
            if self
                .connections
                .get(&client)
                .is_some_and(|c| c.demand != demand)
            {
                changed.push((client, demand));
            }
        }
        for (client, demand) in changed {
            if let Some(c) = self.connections.get_mut(&client) {
                c.demand = demand;
            }
            self.send(
                client,
                WireMessage {
                    capabilities: demand,
                    ..WireMessage::of("demand")
                },
            );
        }
    }

    fn others_in_world(&self, sender: ConnectionId) -> Vec<ConnectionId> {
        self.registered
            .iter()
            .copied()
            .filter(|&c| c != sender && self.same_world(c, sender))
            .collect()
    }

    /// One message from `sender`. An error closes the sender's connection
    /// (its text is reported).
    ///
    /// # Errors
    /// The message breaks the protocol.
    pub fn handle(
        &mut self,
        sender: ConnectionId,
        message: WireMessage,
        now_ms: i64,
    ) -> Result<(), String> {
        if message.kind == "register" {
            return self.register(sender, message);
        }
        let Some(from) = self.connections.get(&sender) else {
            return Ok(());
        };
        let Some(sender_id) = from.id else {
            return Err(invalid("Unregistered peer."));
        };
        match message.kind.as_str() {
            "state" => {
                validate_client(&message.client)?;
                if message.client.player_id != from.client.player_id
                    || message.client.world_name != from.client.world_name
                {
                    return Err(invalid("State identity changed without registration."));
                }
                if let Some(c) = self.connections.get_mut(&sender) {
                    c.client = message.client;
                }
                for other in self.others_in_world(sender) {
                    if self.needs_state(other) {
                        self.send_state(other, sender, false);
                    }
                }
            }
            "cast" if from.capabilities & CASTS != 0 => {
                let cast = &message.cast;
                if cast.caster_id != from.client.player_id
                    || cast.target_id == 0
                    || cast.spell_id == 0
                    || cast.effective_skill < 0
                    || !cast.seconds_remaining.is_finite()
                    || cast.seconds_remaining < 0.0
                    || cast.seconds_remaining > 86_400.0
                {
                    return Err(invalid("Invalid peer cast."));
                }
                let forwarded = WireMessage {
                    id: sender_id,
                    client: from.client.clone(),
                    created_unix_ms: now_ms,
                    ..message
                };
                for other in self.others_in_world(sender) {
                    if self.capabilities(other) & CASTS != 0 {
                        self.send(other, forwarded.clone());
                    }
                }
            }
            "command" => {
                let line_length = message.line.encode_utf16().count();
                if message.id.is_empty()
                    || line_length == 0
                    || line_length > MAXIMUM_LINE
                    || message.line.chars().any(char::is_control)
                    || message.tags.len() > 16
                    || message.tags.iter().any(|t| t.encode_utf16().count() > 64)
                    || !(0..=60_000).contains(&message.delay_ms)
                    || now_ms - message.created_unix_ms > COMMAND_LIFETIME_MS
                {
                    return Err(invalid("Invalid peer command."));
                }
                let mut targets: Vec<ConnectionId> = self
                    .others_in_world(sender)
                    .into_iter()
                    .filter(|&c| {
                        let Some(target) = self.connections.get(&c) else {
                            return false;
                        };
                        target.capabilities & COMMANDS != 0
                            && (message.tags.is_empty()
                                || message.tags.iter().any(|tag| {
                                    target
                                        .client
                                        .tags
                                        .iter()
                                        .any(|t| t.eq_ignore_ascii_case(tag))
                                }))
                    })
                    .collect();
                targets.sort_by_key(|c| {
                    let target = &self.connections[c];
                    (target.client.client_id, target.id.unwrap_or_default().0)
                });
                let client = from.client.clone();
                let mut accepted = true;
                for (place, target) in targets.into_iter().enumerate() {
                    let delay = i32::try_from(place + 1)
                        .unwrap_or(i32::MAX)
                        .saturating_mul(message.delay_ms);
                    accepted &= self.send(
                        target,
                        WireMessage {
                            client: client.clone(),
                            delay_ms: delay,
                            ..message.clone()
                        },
                    );
                }
                self.send(
                    sender,
                    WireMessage {
                        id: message.id,
                        accepted,
                        ..WireMessage::of("ack")
                    },
                );
            }
            _ => {}
        }
        Ok(())
    }

    fn register(&mut self, sender: ConnectionId, message: WireMessage) -> Result<(), String> {
        if message.id.is_empty() || message.capabilities & !ALL_CAPABILITIES != 0 {
            return Err(invalid("Invalid peer registration."));
        }
        let Some(current) = self.connections.get(&sender).map(|c| c.id) else {
            return Ok(());
        };
        match current {
            None => {
                let taken = self
                    .registered
                    .iter()
                    .any(|c| self.connections.get(c).and_then(|c| c.id) == Some(message.id));
                if self.registered.len() >= MAXIMUM_CLIENTS || taken {
                    return Err(invalid("Peer limit or duplicate identity."));
                }
                if let Some(c) = self.connections.get_mut(&sender) {
                    c.id = Some(message.id);
                }
                self.registered.push(sender);
            }
            Some(id) if id != message.id => return Err(invalid("Peer identity changed.")),
            Some(_) => {}
        }
        validate_client(&message.client)?;
        let previous = self.connections[&sender].client.clone();
        if previous.player_id != 0
            && (previous.player_id != message.client.player_id
                || previous.world_name != message.client.world_name)
        {
            for other in self.others_in_world(sender) {
                self.send(
                    other,
                    WireMessage {
                        id: message.id,
                        ..WireMessage::of("remove")
                    },
                );
            }
        }
        if let Some(c) = self.connections.get_mut(&sender) {
            c.client = message.client;
            c.capabilities = message.capabilities;
        }
        // A client knows what it must publish before another can see the
        // new listener and act on it.
        self.update_demand();
        for other in self.others_in_world(sender) {
            if self.needs_state(sender) && self.participates(other) {
                self.send_state(sender, other, true);
            }
            if self.needs_state(other) && self.participates(sender) {
                self.send_state(other, sender, false);
            }
        }
        self.send(sender, WireMessage::of("ready"));
        Ok(())
    }

    /// A connection ended: the others in its world hear it is gone.
    pub fn close(&mut self, connection: ConnectionId) {
        let id = self.connections.get(&connection).and_then(|c| c.id);
        if let Some(at) = self.registered.iter().position(|c| *c == connection) {
            let others: Vec<ConnectionId> = self
                .registered
                .iter()
                .copied()
                .filter(|c| *c != connection)
                .collect();
            for other in others {
                self.send(
                    other,
                    WireMessage {
                        id: id.unwrap_or_default(),
                        ..WireMessage::of("remove")
                    },
                );
            }
            self.registered.remove(at);
        }
        if let Some(c) = self.connections.remove(&connection) {
            c.outbox.dispose();
        }
        self.update_demand();
    }
}

fn now_unix_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}

struct Shared {
    hub: Mutex<Hub>,
    last_activity: Mutex<Instant>,
    report: Box<dyn Fn(&str) + Send + Sync>,
}

fn serve_connection(shared: &Arc<Shared>, connection: ConnectionId, stream: LocalStream) {
    let outbox = Arc::new(PeerOutbox::default());
    let closer = stream.clone();
    shared
        .hub
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .open(
            connection,
            Arc::clone(&outbox),
            Box::new(move || closer.shutdown()),
        );
    let writer_stream = stream.clone();
    let writer_outbox = Arc::clone(&outbox);
    let writer_shared = Arc::clone(shared);
    let writer = std::thread::Builder::new()
        .name("peer-hub-writer".into())
        .spawn(move || {
            while !writer_outbox.is_disposed() {
                let Some(message) = writer_outbox.next(Duration::from_millis(250)) else {
                    continue;
                };
                if let Err(error) = peer_wire::write(&writer_stream, &message) {
                    (writer_shared.report)(&format!("Peer hub writer closed: {error}"));
                    break;
                }
            }
            writer_stream.shutdown();
        });
    loop {
        match peer_wire::read(&stream) {
            Ok(Some(message)) => {
                let result = shared
                    .hub
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .handle(connection, message, now_unix_ms());
                if let Err(text) = result {
                    (shared.report)(&format!("Peer hub connection closed: {text}"));
                    break;
                }
            }
            Ok(None) => break,
            Err(error) => {
                if !outbox.is_disposed() {
                    (shared.report)(&format!("Peer hub connection closed: {error}"));
                }
                break;
            }
        }
    }
    shared
        .hub
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .close(connection);
    *shared
        .last_activity
        .lock()
        .unwrap_or_else(PoisonError::into_inner) = Instant::now();
    stream.shutdown();
    if let Ok(writer) = writer {
        let _ = writer.join();
    }
}

/// Runs the hub on `endpoint` until it has had no client for
/// [`IDLE_STOP`]. Returns at once when another hub holds the endpoint.
///
/// # Errors
/// The endpoint cannot be prepared or listened on.
pub fn serve(
    endpoint: &LocalEndpoint,
    report: impl Fn(&str) + Send + Sync + 'static,
) -> io::Result<()> {
    endpoint.prepare()?;
    let lock = File::options()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(endpoint.lock_path())?;
    if lock.try_lock().is_err() {
        // Another hub owns this endpoint.
        return Ok(());
    }
    let mut listener = endpoint.listen()?;
    let shared = Arc::new(Shared {
        hub: Mutex::new(Hub::default()),
        last_activity: Mutex::new(Instant::now()),
        report: Box::new(report),
    });
    let mut next: ConnectionId = 0;
    let mut workers = Vec::new();
    loop {
        let idle = shared
            .hub
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .client_count()
            == 0
            && shared
                .last_activity
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .elapsed()
                > IDLE_STOP;
        if idle {
            break;
        }
        if let Some(stream) = listener.accept(Duration::from_secs(1))? {
            *shared
                .last_activity
                .lock()
                .unwrap_or_else(PoisonError::into_inner) = Instant::now();
            next += 1;
            let connection = next;
            let worker_shared = Arc::clone(&shared);
            workers.retain(|w: &std::thread::JoinHandle<()>| !w.is_finished());
            if let Ok(worker) = std::thread::Builder::new()
                .name("peer-hub-reader".into())
                .spawn(move || serve_connection(&worker_shared, connection, stream))
            {
                workers.push(worker);
            }
        }
    }
    drop(listener);
    drop(lock);
    Ok(())
}
