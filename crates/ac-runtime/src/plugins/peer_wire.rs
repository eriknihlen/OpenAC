//! What the clients and their peer hub say to each other (OpenAC
//! `PeerHubProtocol`, `PeerWireMessage`, `PeerOutbox`): one JSON object per
//! message, after its length as four little-endian bytes, never more than
//! 64 KB; and the queue a connection's writer drains, where a client's
//! newer state replaces its older one and events keep their order, refusing
//! to overflow.

use std::collections::VecDeque;
use std::fmt::Write as _;
use std::io;
use std::sync::{Condvar, Mutex, PoisonError};
use std::time::Duration;

use ac_host::json::{self, Value};
use ac_plugin::abi::peers::{NetworkClient, PeerCast};
use ac_plugin::abi::world::Position;
use ac_plugin_net::local::LocalStream;

use super::peers::{InstanceId, Members, object};

/// The largest message, bytes.
pub const MAXIMUM_MESSAGE_BYTES: usize = 65_536;
/// How many events (or distinct clients' states) a queue holds.
pub const OUTBOX_CAPACITY: usize = 256;

/// Publish this character and receive the other participants' state.
pub const CLIENT_STATE: u32 = 1;
/// Publish casts and receive the other participants' casts.
pub const CASTS: u32 = 2;
/// Receive the lines other clients broadcast.
pub const COMMANDS: u32 = 4;
/// Connect to broadcast without receiving.
pub const SEND_COMMANDS: u32 = 8;
/// Every capability.
pub const ALL_CAPABILITIES: u32 = CLIENT_STATE | CASTS | COMMANDS | SEND_COMMANDS;

/// One message. Which members matter depends on `kind`: `register`
/// (identity, capabilities, client), `ready`, `demand` (capabilities),
/// `state` (identity, client, initial), `remove` (identity), `cast`
/// (identity, client, cast, created), `command` (identity, client, line,
/// tags, delay, created), `ack` (identity, accepted).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct WireMessage {
    /// What it is.
    pub kind: String,
    /// A client's instance, or a command's id.
    pub id: InstanceId,
    /// Capabilities registered, or the demand.
    pub capabilities: u32,
    /// A client.
    pub client: NetworkClient,
    /// A cast.
    pub cast: PeerCast,
    /// A command line.
    pub line: String,
    /// The labels a command is aimed at.
    pub tags: Vec<String>,
    /// A command's wait for this recipient, milliseconds.
    pub delay_ms: i32,
    /// When it was sent or forwarded, UTC milliseconds.
    pub created_unix_ms: i64,
    /// The hub took a command for every recipient.
    pub accepted: bool,
    /// The state a new listener is first sent.
    pub initial: bool,
}

impl WireMessage {
    /// A message of `kind` with nothing else in it.
    #[must_use]
    pub fn of(kind: &str) -> Self {
        Self {
            kind: kind.to_owned(),
            ..Self::default()
        }
    }
}

fn write_strings(out: &mut String, items: &[String]) {
    out.push('[');
    for (i, item) in items.iter().enumerate() {
        if i > 0 {
            out.push(',');
        }
        json::write_string(out, item);
    }
    out.push(']');
}

fn write_client(out: &mut String, c: &NetworkClient) {
    let _ = write!(
        out,
        "{{\"ClientId\":{},\"PlayerId\":{},\"Name\":",
        c.client_id, c.player_id
    );
    json::write_string(out, &c.name);
    out.push_str(",\"WorldName\":");
    json::write_string(out, &c.world_name);
    out.push_str(",\"Tags\":");
    write_strings(out, &c.tags);
    let p = &c.position;
    let _ = write!(
        out,
        ",\"CellId\":{},\"EastWest\":{},\"NorthSouth\":{},\"Elevation\":{},\"HeadingDegrees\":{},\
         \"IsOutdoor\":{},\"Heading\":{},\"CurrentHealth\":{},\"CurrentMana\":{},\"CurrentStamina\":{},\
         \"MaxHealth\":{},\"MaxMana\":{},\"MaxStamina\":{}}}",
        p.cell_id,
        p.east_west,
        p.north_south,
        p.elevation,
        p.heading_degrees,
        p.is_outdoor,
        c.heading,
        c.current_health,
        c.current_mana,
        c.current_stamina,
        c.max_health,
        c.max_mana,
        c.max_stamina
    );
}

/// The message's JSON.
#[must_use]
pub fn encode(m: &WireMessage) -> String {
    let mut out = String::from("{\"Kind\":");
    json::write_string(&mut out, &m.kind);
    out.push_str(",\"Id\":");
    json::write_string(&mut out, &m.id.text());
    let _ = write!(out, ",\"Capabilities\":{},\"Client\":", m.capabilities);
    write_client(&mut out, &m.client);
    let c = &m.cast;
    let _ = write!(
        out,
        ",\"Cast\":{{\"Sequence\":{},\"ClientId\":{},\"CasterObjectId\":{},\"TargetObjectId\":{},\
         \"SpellId\":{},\"EffectiveSkill\":{},\"SecondsRemaining\":{},\"Landed\":{}}},\"Line\":",
        c.sequence,
        c.client_id,
        c.caster_id,
        c.target_id,
        c.spell_id,
        c.effective_skill,
        c.seconds_remaining,
        c.landed
    );
    json::write_string(&mut out, &m.line);
    out.push_str(",\"Tags\":");
    write_strings(&mut out, &m.tags);
    let _ = write!(
        out,
        ",\"DelayMilliseconds\":{},\"CreatedUnixMs\":{},\"Accepted\":{},\"Initial\":{}}}",
        m.delay_ms, m.created_unix_ms, m.accepted, m.initial
    );
    out
}

fn read_client(m: &Members<'_>) -> Option<NetworkClient> {
    let heading: f32 = m.number("Heading")?;
    Some(NetworkClient {
        client_id: m.number("ClientId")?,
        player_id: m.number("PlayerId")?,
        name: m.string("Name")??,
        world_name: m.string("WorldName")??,
        position: Position {
            cell_id: m.number("CellId")?,
            east_west: m.number("EastWest")?,
            north_south: m.number("NorthSouth")?,
            elevation: m.number("Elevation")?,
            heading_degrees: m.number("HeadingDegrees")?,
            is_outdoor: m.boolean("IsOutdoor")?,
        },
        tags: m.strings("Tags")??,
        current_health: m.number("CurrentHealth")?,
        current_mana: m.number("CurrentMana")?,
        current_stamina: m.number("CurrentStamina")?,
        max_health: m.number("MaxHealth")?,
        max_mana: m.number("MaxMana")?,
        max_stamina: m.number("MaxStamina")?,
        heading,
        is_remote: false,
    })
}

/// A message from its JSON; none for bytes that are not one.
#[must_use]
pub fn decode(bytes: &[u8]) -> Option<WireMessage> {
    let root = json::parse(bytes).ok()?;
    let m = object(&root)?;
    let client = match m.get("Client").map(|n| &n.value) {
        None | Some(Value::Null) => NetworkClient::default(),
        Some(_) => read_client(&object(m.get("Client")?)?)?,
    };
    let cast = match m.get("Cast").map(|n| &n.value) {
        None | Some(Value::Null) => PeerCast::default(),
        Some(_) => {
            let c = object(m.get("Cast")?)?;
            PeerCast {
                sequence: c.number("Sequence")?,
                client_id: c.number("ClientId")?,
                caster_id: c.number("CasterObjectId")?,
                target_id: c.number("TargetObjectId")?,
                spell_id: c.number("SpellId")?,
                effective_skill: c.number("EffectiveSkill")?,
                seconds_remaining: c.number("SecondsRemaining")?,
                landed: c.boolean("Landed")?,
                is_remote: false,
            }
        }
    };
    let id = m.string("Id")??;
    Some(WireMessage {
        kind: m.string("Kind")??,
        id: if id.is_empty() {
            InstanceId::default()
        } else {
            InstanceId::parse(&id)?
        },
        capabilities: m.number("Capabilities")?,
        client,
        cast,
        line: m.string("Line")??,
        tags: m.strings("Tags")??,
        delay_ms: m.number("DelayMilliseconds")?,
        created_unix_ms: m.number("CreatedUnixMs")?,
        accepted: m.boolean("Accepted")?,
        initial: m.boolean("Initial")?,
    })
}

/// Reads one message: none at the end of the stream (before a message
/// starts).
///
/// # Errors
/// The stream failed, ended inside a message, or carried a length or JSON
/// that is not a message.
pub fn read(stream: &LocalStream) -> io::Result<Option<WireMessage>> {
    let mut prefix = [0u8; 4];
    if stream.read(&mut prefix[..1])? == 0 {
        return Ok(None);
    }
    stream.read_exact(&mut prefix[1..])?;
    let length = u32::from_le_bytes(prefix) as usize;
    if length == 0 || length > MAXIMUM_MESSAGE_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Invalid peer message length.",
        ));
    }
    let mut body = vec![0u8; length];
    stream.read_exact(&mut body)?;
    decode(&body)
        .map(Some)
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "Invalid peer message."))
}

/// Writes one message.
///
/// # Errors
/// The message is over 64 KB, or the stream failed.
pub fn write(stream: &LocalStream, message: &WireMessage) -> io::Result<()> {
    let body = encode(message);
    if body.len() > MAXIMUM_MESSAGE_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Peer message is too large.",
        ));
    }
    let mut frame = (body.len() as u32).to_le_bytes().to_vec();
    frame.extend_from_slice(body.as_bytes());
    stream.write_all(&frame)
}

#[derive(Debug, Default)]
struct Queue {
    events: VecDeque<WireMessage>,
    /// The latest state per client, oldest first.
    state: VecDeque<WireMessage>,
    disposed: bool,
}

/// One connection's outgoing messages, drained by its writer.
#[derive(Debug, Default)]
pub struct PeerOutbox {
    queue: Mutex<Queue>,
    ready: Condvar,
}

impl PeerOutbox {
    /// Queues `message`: a client's state replaces its queued state; any
    /// other message keeps its place. False when the queue is full (256
    /// events, or states of 256 clients) or closed.
    pub fn enqueue(&self, message: WireMessage) -> bool {
        let mut queue = self.queue.lock().unwrap_or_else(PoisonError::into_inner);
        if queue.disposed {
            return false;
        }
        if message.kind == "state" && !message.initial {
            if let Some(held) = queue.state.iter_mut().find(|m| m.id == message.id) {
                *held = message;
            } else {
                if queue.state.len() >= OUTBOX_CAPACITY {
                    return false;
                }
                queue.state.push_back(message);
            }
        } else {
            if queue.events.len() >= OUTBOX_CAPACITY {
                return false;
            }
            if message.kind == "remove" {
                queue.state.retain(|m| m.id != message.id);
            }
            queue.events.push_back(message);
        }
        drop(queue);
        self.ready.notify_all();
        true
    }

    /// The next message to write, events before state, waiting up to
    /// `wait` for one; none when there is none yet or the queue is closed.
    pub fn next(&self, wait: Duration) -> Option<WireMessage> {
        let mut queue = self.queue.lock().unwrap_or_else(PoisonError::into_inner);
        if !queue.disposed && queue.events.is_empty() && queue.state.is_empty() {
            queue = self
                .ready
                .wait_timeout(queue, wait)
                .unwrap_or_else(PoisonError::into_inner)
                .0;
        }
        if queue.disposed {
            return None;
        }
        queue.events.pop_front().or_else(|| queue.state.pop_front())
    }

    /// Closes the queue and drops what it held.
    pub fn dispose(&self) {
        let mut queue = self.queue.lock().unwrap_or_else(PoisonError::into_inner);
        queue.disposed = true;
        queue.events.clear();
        queue.state.clear();
        drop(queue);
        self.ready.notify_all();
    }

    /// Whether the queue was closed.
    #[must_use]
    pub fn is_disposed(&self) -> bool {
        self.queue
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .disposed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // OpenAC `PeerHubTests.OutboxCoalescesStateAndRefusesEventOverflow`.
    #[test]
    fn the_outbox_coalesces_state_and_refuses_event_overflow() {
        let outbox = PeerOutbox::default();
        let id = InstanceId([7; 16]);
        for _ in 0..1000 {
            assert!(outbox.enqueue(WireMessage {
                id,
                ..WireMessage::of("state")
            }));
        }
        for _ in 0..256 {
            assert!(outbox.enqueue(WireMessage::of("cast")));
        }
        assert!(!outbox.enqueue(WireMessage::of("command")));
        // Events first, then the one state left of the thousand.
        for _ in 0..256 {
            assert_eq!(outbox.next(Duration::ZERO).unwrap().kind, "cast");
        }
        assert_eq!(outbox.next(Duration::ZERO).unwrap().kind, "state");
        assert_eq!(outbox.next(Duration::ZERO), None);
        outbox.dispose();
        assert!(!outbox.enqueue(WireMessage::of("cast")));
    }

    #[test]
    fn a_message_reads_back_as_written() {
        let message = WireMessage {
            kind: "command".into(),
            id: InstanceId([3; 16]),
            capabilities: CASTS | COMMANDS,
            client: NetworkClient {
                client_id: 9,
                player_id: 0x5000_0001,
                name: "Acdream \"one\"".into(),
                world_name: "Coldeve".into(),
                position: Position {
                    cell_id: 0xA9B4_0031,
                    east_west: 33.5,
                    north_south: -12.25,
                    elevation: 0.5,
                    heading_degrees: 90.0,
                    is_outdoor: true,
                },
                tags: vec!["army".into()],
                current_health: 10,
                max_health: 20,
                heading: 90.0,
                ..NetworkClient::default()
            },
            cast: PeerCast {
                sequence: 4,
                caster_id: 1,
                target_id: 2,
                spell_id: 3,
                effective_skill: 300,
                seconds_remaining: 12.5,
                landed: true,
                ..PeerCast::default()
            },
            line: "/say hi".into(),
            tags: vec!["army".into(), "mages".into()],
            delay_ms: 500,
            created_unix_ms: 1_760_000_000_123,
            accepted: true,
            initial: true,
        };
        assert_eq!(decode(encode(&message).as_bytes()), Some(message));
        assert_eq!(decode(b"{\"Kind\":1}"), None);
        assert_eq!(decode(b"not json"), None);
    }
}
