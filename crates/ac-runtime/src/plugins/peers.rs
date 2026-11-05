//! The other clients on this computer (OpenAC `LocalPluginPeerRegistry`):
//! each client keeps one small JSON note in a shared folder, rewritten every
//! few seconds (and soon after a cast or a broadcast), saying who it is,
//! where its character stands, the labels it answers to, and rings of its
//! recent casts and broadcast command lines. Every client reads the others'
//! notes: a note older than the staleness window speaks for nobody, a cast
//! or line is taken in once by its sequence, and nothing in a note is
//! trusted that this client would not have written itself.
//!
//! The registry is fed the wall clock (`now_ms`, UTC milliseconds) on every
//! call; it reads and writes the folder itself. In a session the registry
//! is kept in memory instead ([`PeerRegistry::in_memory`], OpenAC
//! `memoryOnly`): no note is written, and the other clients are what the
//! peer hub pushes ([`super::peer_transport`]); the folder notes remain
//! OpenAC's registry without a transport.

use std::collections::{HashMap, VecDeque};
use std::fs;
use std::path::{Path, PathBuf};

use ac_host::json::{self, Node, Value};
use ac_plugin::abi::peers::{NetworkClient, PeerCast, PeerCommand};
use ac_plugin::abi::world::Position;

/// How long a note, a cast or a line is believed, milliseconds.
pub const STALE_AFTER_MS: i64 = 15_000;
/// How many casts one client's note carries.
pub const CAST_RING_CAPACITY: usize = 32;
/// How many casts read from others are kept to hand back.
pub const OBSERVED_CAST_CAPACITY: usize = 128;
/// How often a client looks for lines the others asked it to run, seconds.
pub const COMMAND_POLL_SECONDS: f64 = 0.25;
/// How many broadcast lines one client's note carries.
pub const COMMAND_RING_CAPACITY: usize = 32;
/// How many lines read from others are kept to hand back.
pub const OBSERVED_COMMAND_CAPACITY: usize = 128;
/// The longest line a broadcast may carry.
pub const MAXIMUM_COMMAND_LINE_LENGTH: usize = 512;
/// The longest label.
pub const MAXIMUM_TAG_LENGTH: usize = 64;
/// How many labels one broadcast may be aimed at.
pub const MAXIMUM_COMMAND_TAGS: usize = 16;
/// The longest stagger a broadcast may ask for per place, milliseconds.
pub const MAXIMUM_COMMAND_DELAY_MS: i32 = 60_000;
/// The shortest gap between two notes written for a cast or a line.
pub const CAST_WRITE_DEBOUNCE_MS: i64 = 250;
/// How often the note is rewritten anyway, seconds; also how long a write
/// that could not be done holds the next back.
pub const HEARTBEAT_SECONDS: f64 = 5.0;
/// The longest effect a cast may claim, seconds.
pub const MAXIMUM_CAST_DURATION_SECONDS: f64 = 24.0 * 60.0 * 60.0;
/// The highest sequence a cast or line may claim.
pub const MAXIMUM_CAST_SEQUENCE: i64 = 1_000_000_000;
/// How many peers plugins may import at once; a stale one gives its place
/// up to a new one.
pub const MAXIMUM_REMOTE_CLIENTS: usize = 256;
/// The longest name or world name a note or an import may carry.
pub const MAXIMUM_NAME_LENGTH: usize = 128;
/// How many labels one client may answer to.
pub const MAXIMUM_CLIENT_TAGS: usize = 128;
/// The largest note read, bytes.
const MAXIMUM_DOCUMENT_BYTES: u64 = 64 * 1024;
/// The earliest and latest stamps .NET can represent, milliseconds.
const EARLIEST_UNIX_MS: i64 = -62_135_596_800_000;
const LATEST_UNIX_MS: i64 = 253_402_300_799_999;

/// A client's instance id: a GUID, as the 16 bytes its text reads.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash)]
pub struct InstanceId(pub [u8; 16]);

impl InstanceId {
    /// The client id the instance runs as: the GUID's first field (never
    /// 0).
    #[must_use]
    pub fn client_id(self) -> u32 {
        let id = u32::from_be_bytes([self.0[0], self.0[1], self.0[2], self.0[3]]);
        id.max(1)
    }

    pub(crate) fn is_empty(self) -> bool {
        self.0 == [0; 16]
    }

    /// The 32 hex digits a note's file name carries.
    fn compact(self) -> String {
        self.0.iter().map(|b| format!("{b:02x}")).collect()
    }

    /// The GUID's text, `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`.
    pub(crate) fn text(self) -> String {
        let h = self.compact();
        format!(
            "{}-{}-{}-{}-{}",
            &h[0..8],
            &h[8..12],
            &h[12..16],
            &h[16..20],
            &h[20..32]
        )
    }

    pub(crate) fn parse(text: &str) -> Option<Self> {
        let digits: String = text.chars().filter(|c| *c != '-').collect();
        if digits.len() != 32 {
            return None;
        }
        let mut bytes = [0u8; 16];
        for (i, byte) in bytes.iter_mut().enumerate() {
            *byte = u8::from_str_radix(digits.get(i * 2..i * 2 + 2)?, 16).ok()?;
        }
        Some(Self(bytes))
    }
}

/// A cast this client announces, before the registry numbers it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LocalCast {
    /// Its caster.
    pub caster_id: u32,
    /// Its target.
    pub target_id: u32,
    /// The spell.
    pub spell_id: u32,
    /// The skill.
    pub effective_skill: i32,
    /// The effect's whole length, seconds; 0 for an attempt.
    pub duration_seconds: f64,
    /// It landed.
    pub landed: bool,
}

/// A line this client broadcasts, before the registry numbers it.
#[derive(Debug, Clone, PartialEq)]
pub struct LocalCommand {
    /// Its sender's character.
    pub sender_id: u32,
    /// The labels it is aimed at.
    pub tags: Vec<String>,
    /// The line.
    pub line: String,
    /// The stagger per place, milliseconds.
    pub delay_ms: i32,
}

/// A line read from another client, with this client's wait before it.
#[derive(Debug, Clone, PartialEq)]
pub struct RemoteCommand {
    /// What a plugin reading it is handed.
    pub command: PeerCommand,
    /// How long this client waits before running it, milliseconds.
    pub stagger_ms: i32,
}

#[derive(Debug, Clone, PartialEq)]
struct CastEntry {
    sequence: i64,
    at_unix_ms: i64,
    caster_id: u32,
    target_id: u32,
    spell_id: u32,
    effective_skill: i32,
    duration_seconds: f64,
    landed: bool,
}

#[derive(Debug, Clone, PartialEq)]
struct CommandEntry {
    sequence: i64,
    at_unix_ms: i64,
    sender_id: u32,
    tags: Vec<String>,
    line: String,
    delay_ms: i32,
}

/// A note as it travels (OpenAC's `PeerDocument`).
#[derive(Debug, Clone, PartialEq)]
struct Document {
    instance: InstanceId,
    updated_unix_ms: i64,
    client: NetworkClient,
    casts: Vec<CastEntry>,
    commands: Vec<CommandEntry>,
}

#[derive(Debug, Clone, Copy, Default)]
struct ObservedPeer {
    highest_cast: i64,
    highest_command: i64,
    last_seen_ms: i64,
}

#[derive(Debug, Clone)]
struct ObservedCast {
    sequence: i64,
    client_id: u32,
    is_remote: bool,
    entry: CastEntry,
}

#[derive(Debug, Clone)]
struct ObservedCommand {
    sequence: i64,
    client_id: u32,
    is_remote: bool,
    entry: CommandEntry,
    aimed_at: Vec<String>,
    stagger_ms: i32,
}

/// A peer a plugin imported, held as the note a neighbour would have
/// written (so staleness, world, labels and cursors apply through the same
/// code), with the counters its rings are numbered by.
#[derive(Debug, Clone)]
struct RemotePeer {
    document: Document,
    /// Not a file: a key no folder scan can report, so a cursor on an
    /// imported peer is never moved by a note.
    path: PathBuf,
    cast_sequence: i64,
    command_sequence: i64,
    /// The last read listed this copy rather than a note.
    listed: bool,
}

/// What a cast is read back as now: what is left of a success's duration.
fn project_cast(
    sequence: i64,
    client_id: u32,
    is_remote: bool,
    entry: &CastEntry,
    now_ms: i64,
) -> PeerCast {
    #[allow(clippy::cast_precision_loss)]
    let elapsed = ((now_ms - entry.at_unix_ms) as f64 / 1000.0).max(0.0);
    PeerCast {
        sequence,
        client_id,
        caster_id: entry.caster_id,
        target_id: entry.target_id,
        spell_id: entry.spell_id,
        effective_skill: entry.effective_skill,
        seconds_remaining: (entry.duration_seconds - elapsed).clamp(0.0, entry.duration_seconds),
        landed: entry.landed,
        is_remote,
    }
}

/// This client's note and what it read from the others.
#[derive(Debug)]
pub struct PeerRegistry {
    directory: PathBuf,
    path: PathBuf,
    instance: InstanceId,
    client_id: u32,
    ring: VecDeque<CastEntry>,
    command_ring: VecDeque<CommandEntry>,
    observed_peers: HashMap<(PathBuf, InstanceId), ObservedPeer>,
    observed_casts: Vec<ObservedCast>,
    observed_commands: Vec<ObservedCommand>,
    cast_sequence: i64,
    command_sequence: i64,
    observed_sequence: i64,
    observed_command_sequence: i64,
    cast_write_pending: bool,
    command_write_pending: bool,
    last_write_ms: Option<i64>,
    hold_writes_until_ms: Option<i64>,
    temporary: u64,
    /// Peers a plugin imported from somewhere this client cannot read, by
    /// their character.
    remote_peers: HashMap<u32, RemotePeer>,
    /// Identities handed to imported peers so far (the next one's seed).
    remote_identities: u64,
    /// No note folder: the other clients arrive from the peer hub
    /// (OpenAC `memoryOnly`).
    memory_only: bool,
    /// The other clients the hub pushed, as the notes they stand for.
    pushed: Vec<(InstanceId, Document)>,
}

/// The labels as they travel: trimmed, blanks dropped, repeats (any case)
/// folded, at most 128.
pub(super) fn normalize_tags(tags: &[String]) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    for tag in tags {
        let tag = tag.trim();
        if tag.is_empty() || out.iter().any(|t| t.eq_ignore_ascii_case(tag)) {
            continue;
        }
        out.push(tag.to_owned());
        if out.len() == 128 {
            break;
        }
    }
    out
}

fn tags_well_formed(tags: &[String]) -> bool {
    tags.iter()
        .all(|t| !t.trim().is_empty() && t.encode_utf16().count() <= MAXIMUM_TAG_LENGTH)
}

fn aimed_at(aimed: &[String], client_tags: &[String]) -> bool {
    aimed.is_empty()
        || aimed
            .iter()
            .any(|tag| client_tags.iter().any(|t| t.eq_ignore_ascii_case(tag)))
}

fn cast_well_formed(e: &CastEntry) -> bool {
    e.sequence > 0
        && e.sequence <= MAXIMUM_CAST_SEQUENCE
        && (EARLIEST_UNIX_MS..=LATEST_UNIX_MS).contains(&e.at_unix_ms)
        && e.caster_id != 0
        && e.target_id != 0
        && e.spell_id != 0
        && e.effective_skill >= 0
        && e.duration_seconds.is_finite()
        && e.duration_seconds >= 0.0
        && e.duration_seconds <= MAXIMUM_CAST_DURATION_SECONDS
        && (!e.landed || e.duration_seconds > 0.0)
}

fn command_well_formed(e: &CommandEntry) -> bool {
    e.sequence > 0
        && e.sequence <= MAXIMUM_CAST_SEQUENCE
        && (EARLIEST_UNIX_MS..=LATEST_UNIX_MS).contains(&e.at_unix_ms)
        && e.sender_id != 0
        && (0..=MAXIMUM_COMMAND_DELAY_MS).contains(&e.delay_ms)
        && !e.line.trim().is_empty()
        && e.line.encode_utf16().count() <= MAXIMUM_COMMAND_LINE_LENGTH
        && !e.line.chars().any(char::is_control)
        && e.tags.len() <= MAXIMUM_COMMAND_TAGS
        && tags_well_formed(&e.tags)
}

fn fresh(at_unix_ms: i64, now_ms: i64) -> bool {
    let age = now_ms.saturating_sub(at_unix_ms);
    (-STALE_AFTER_MS..=STALE_AFTER_MS).contains(&age)
}

fn is_recent(document: &Document, now_ms: i64) -> bool {
    document.updated_unix_ms >= now_ms - STALE_AFTER_MS
}

fn same_world(document: &Document, world: &str) -> bool {
    document.client.world_name.to_lowercase() == world.to_lowercase()
}

impl PeerRegistry {
    /// A registry keeping its note in `directory`, running as `instance`.
    #[must_use]
    pub fn new(directory: &Path, instance: InstanceId) -> Self {
        Self {
            directory: directory.to_path_buf(),
            path: directory.join(format!("peer-{}.json", instance.compact())),
            instance,
            client_id: instance.client_id(),
            ring: VecDeque::new(),
            command_ring: VecDeque::new(),
            observed_peers: HashMap::new(),
            observed_casts: Vec::new(),
            observed_commands: Vec::new(),
            cast_sequence: 0,
            command_sequence: 0,
            observed_sequence: 0,
            observed_command_sequence: 0,
            cast_write_pending: false,
            command_write_pending: false,
            last_write_ms: None,
            hold_writes_until_ms: None,
            temporary: 0,
            remote_peers: HashMap::new(),
            remote_identities: 0,
            memory_only: false,
            pushed: Vec::new(),
        }
    }

    /// A registry with no note folder: the other clients are what the
    /// peer hub pushes (OpenAC `LocalPluginPeerRegistry(directory,
    /// memoryOnly: true)`). Nothing is written or read on disk.
    #[must_use]
    pub fn in_memory(directory: &Path, instance: InstanceId) -> Self {
        Self {
            memory_only: true,
            ..Self::new(directory, instance)
        }
    }

    /// This client's instance.
    #[must_use]
    pub fn instance(&self) -> InstanceId {
        self.instance
    }

    /// This client's id.
    #[must_use]
    pub fn client_id(&self) -> u32 {
        self.client_id
    }

    /// Writes this client's note, replacing what it said before; false
    /// when refused because the position or heading is not a finite number
    /// (no reader would accept it).
    pub fn publish(&mut self, client: &NetworkClient, now_ms: i64) -> bool {
        // State travels through the peer hub instead.
        if self.memory_only {
            return false;
        }
        let p = &client.position;
        let writable = p.east_west.is_finite()
            && p.north_south.is_finite()
            && p.elevation.is_finite()
            && client.heading.is_finite();
        if !writable {
            self.hold_writes_until_ms = Some(now_ms + heartbeat_ms());
            return false;
        }
        let published_through = self.cast_sequence;
        let commands_through = self.command_sequence;
        let document = Document {
            instance: self.instance,
            updated_unix_ms: now_ms,
            client: NetworkClient {
                is_remote: false,
                client_id: self.client_id,
                tags: normalize_tags(&client.tags),
                ..client.clone()
            },
            casts: self.ring.iter().cloned().collect(),
            commands: self.command_ring.iter().cloned().collect(),
        };
        self.temporary += 1;
        let temporary = self.path.with_extension(format!(
            "json.{}-{}.tmp",
            std::process::id(),
            self.temporary
        ));
        let written = fs::create_dir_all(&self.directory)
            .and_then(|()| fs::write(&temporary, write_document(&document)))
            .and_then(|()| fs::rename(&temporary, &self.path))
            .is_ok();
        if written {
            if self.cast_sequence == published_through {
                self.cast_write_pending = false;
            }
            if self.command_sequence == commands_through {
                self.command_write_pending = false;
            }
            self.last_write_ms = Some(now_ms);
            self.hold_writes_until_ms = None;
        } else {
            // The temporary file is this client's own; the old note stands
            // and the next try waits for the heartbeat.
            let _ = fs::remove_file(&temporary);
            self.hold_writes_until_ms = Some(now_ms + heartbeat_ms());
        }
        true
    }

    /// Adds a cast to this client's ring; false for one that makes no sense.
    pub fn record_cast(&mut self, cast: &LocalCast, now_ms: i64) -> bool {
        let entry = CastEntry {
            sequence: self.cast_sequence + 1,
            at_unix_ms: now_ms,
            caster_id: cast.caster_id,
            target_id: cast.target_id,
            spell_id: cast.spell_id,
            effective_skill: cast.effective_skill,
            duration_seconds: cast.duration_seconds,
            landed: cast.landed,
        };
        if !cast_well_formed(&entry) {
            return false;
        }
        self.cast_sequence = entry.sequence;
        self.ring.push_back(entry);
        while self.ring.len() > CAST_RING_CAPACITY {
            self.ring.pop_front();
        }
        self.cast_write_pending = true;
        true
    }

    fn write_due(&self, pending: bool, now_ms: i64) -> bool {
        if self
            .hold_writes_until_ms
            .is_some_and(|until| now_ms < until)
        {
            return false;
        }
        pending
            && self
                .last_write_ms
                .is_none_or(|last| now_ms - last >= CAST_WRITE_DEBOUNCE_MS)
    }

    /// A recorded cast waits and the debounce allows a write.
    #[must_use]
    pub fn is_cast_write_due(&self, now_ms: i64) -> bool {
        self.write_due(self.cast_write_pending, now_ms)
    }

    /// Adds a line to this client's ring; false for one that makes no sense.
    pub fn record_command(&mut self, command: &LocalCommand, now_ms: i64) -> bool {
        let entry = CommandEntry {
            sequence: self.command_sequence + 1,
            at_unix_ms: now_ms,
            sender_id: command.sender_id,
            tags: normalize_tags(&command.tags),
            line: command.line.clone(),
            delay_ms: command.delay_ms,
        };
        if !command_well_formed(&entry) {
            return false;
        }
        self.command_sequence = entry.sequence;
        self.command_ring.push_back(entry);
        while self.command_ring.len() > COMMAND_RING_CAPACITY {
            self.command_ring.pop_front();
        }
        self.command_write_pending = true;
        true
    }

    /// A recorded line waits and the debounce allows a write.
    #[must_use]
    pub fn is_command_write_due(&self, now_ms: i64) -> bool {
        self.write_due(self.command_write_pending, now_ms)
    }

    /// The other clients whose notes are recent, by name then id.
    /// An imported peer that turns out to be `own_player` (the character
    /// this client now plays) is not somebody else.
    pub fn remote_clients(&mut self, own_player: u32, now_ms: i64) -> Vec<NetworkClient> {
        let mut clients: Vec<NetworkClient> = self
            .read_notes(now_ms)
            .into_iter()
            .filter(|(_, d)| {
                is_recent(d, now_ms) && !(d.client.is_remote && d.client.player_id == own_player)
            })
            .map(|(_, d)| d.client)
            .collect();
        clients.sort_by(|a, b| {
            a.name
                .to_lowercase()
                .cmp(&b.name.to_lowercase())
                .then(a.client_id.cmp(&b.client_id))
        });
        clients
    }

    /// The casts others announced, above `after`: only from clients in
    /// `world`, never one put under `own_player`'s name.
    pub fn remote_casts(
        &mut self,
        after: i64,
        world: &str,
        own_player: u32,
        now_ms: i64,
    ) -> Vec<PeerCast> {
        let mut notes = self.read_notes(now_ms);
        notes.sort_by(|a, b| {
            (a.1.client.client_id, a.1.instance.0).cmp(&(b.1.client.client_id, b.1.instance.0))
        });
        for (path, document) in notes {
            if !is_recent(&document, now_ms) || !same_world(&document, world) {
                continue;
            }
            let cursor = (path, document.instance);
            let peer = self
                .observed_peers
                .get(&cursor)
                .copied()
                .unwrap_or_default();
            let mut highest = peer.highest_cast;
            let mut casts = document.casts.clone();
            casts.sort_by_key(|c| c.sequence);
            for entry in casts {
                if entry.sequence <= highest {
                    continue;
                }
                highest = entry.sequence;
                if !cast_well_formed(&entry)
                    || entry.caster_id == own_player
                    || !fresh(entry.at_unix_ms, now_ms)
                {
                    continue;
                }
                self.observed_sequence += 1;
                self.observed_casts.push(ObservedCast {
                    sequence: self.observed_sequence,
                    client_id: document.client.client_id,
                    is_remote: document.client.is_remote,
                    entry,
                });
            }
            self.observed_peers.insert(
                cursor,
                ObservedPeer {
                    highest_cast: highest,
                    last_seen_ms: now_ms,
                    ..peer
                },
            );
        }
        trim_front(&mut self.observed_casts, OBSERVED_CAST_CAPACITY);
        self.forget_stale(now_ms);
        self.observed_casts
            .iter()
            .filter(|o| o.sequence > after)
            .map(|o| project_cast(o.sequence, o.client_id, o.is_remote, &o.entry, now_ms))
            .collect()
    }

    /// The lines others broadcast to a client wearing `own_tags` in
    /// `world`, above `after`, with this client's stagger for each.
    pub fn remote_commands(
        &mut self,
        after: i64,
        world: &str,
        own_player: u32,
        own_tags: &[String],
        now_ms: i64,
    ) -> Vec<RemoteCommand> {
        let notes = self.read_notes(now_ms);
        if !notes.is_empty() {
            let own_tags = normalize_tags(own_tags);
            let mut ordered = notes.clone();
            ordered.sort_by(|a, b| {
                (a.1.client.client_id, a.1.instance.0).cmp(&(b.1.client.client_id, b.1.instance.0))
            });
            for (path, document) in ordered {
                if !is_recent(&document, now_ms) || !same_world(&document, world) {
                    continue;
                }
                let cursor = (path, document.instance);
                let peer = self
                    .observed_peers
                    .get(&cursor)
                    .copied()
                    .unwrap_or_default();
                let mut highest = peer.highest_command;
                let mut lines = document.commands.clone();
                lines.sort_by_key(|c| c.sequence);
                for entry in lines {
                    if entry.sequence <= highest {
                        continue;
                    }
                    highest = entry.sequence;
                    if !command_well_formed(&entry)
                        || entry.sender_id == own_player
                        || !fresh(entry.at_unix_ms, now_ms)
                    {
                        continue;
                    }
                    let aimed = normalize_tags(&entry.tags);
                    if !aimed_at(&aimed, &own_tags) {
                        continue;
                    }
                    let stagger = self.stagger_for(
                        &notes,
                        now_ms,
                        world,
                        document.client.client_id,
                        &aimed,
                        entry.delay_ms,
                    );
                    self.observed_command_sequence += 1;
                    self.observed_commands.push(ObservedCommand {
                        sequence: self.observed_command_sequence,
                        client_id: document.client.client_id,
                        is_remote: document.client.is_remote,
                        entry,
                        aimed_at: aimed,
                        stagger_ms: stagger,
                    });
                }
                self.observed_peers.insert(
                    cursor,
                    ObservedPeer {
                        highest_command: highest,
                        last_seen_ms: now_ms,
                        ..peer
                    },
                );
            }
            trim_front(&mut self.observed_commands, OBSERVED_COMMAND_CAPACITY);
        }
        self.forget_stale(now_ms);
        self.observed_commands
            .iter()
            .filter(|o| o.sequence > after)
            .map(|o| RemoteCommand {
                command: PeerCommand {
                    sequence: o.sequence,
                    client_id: o.client_id,
                    sender_id: o.entry.sender_id,
                    tags: o.aimed_at.clone(),
                    line: o.entry.line.clone(),
                    sent_unix_ms: o.entry.at_unix_ms,
                    is_remote: o.is_remote,
                },
                stagger_ms: o.stagger_ms,
            })
            .collect()
    }

    /// This client's wait: the sender holds place zero, then the
    /// recipients (live clients in the world wearing a label the line is
    /// aimed at) by client id.
    fn stagger_for(
        &self,
        notes: &[(PathBuf, Document)],
        now_ms: i64,
        world: &str,
        sender: u32,
        aimed: &[String],
        delay_ms: i32,
    ) -> i32 {
        if delay_ms <= 0 {
            return 0;
        }
        let ahead = notes
            .iter()
            .filter(|(_, d)| {
                let id = d.client.client_id;
                !d.client.is_remote
                    && id != sender
                    && id < self.client_id
                    && is_recent(d, now_ms)
                    && same_world(d, world)
                    && aimed_at(aimed, &normalize_tags(&d.client.tags))
            })
            .count();
        i32::try_from(ahead + 1)
            .unwrap_or(i32::MAX)
            .saturating_mul(delay_ms)
    }

    /// The casts this client announced above `after` that a neighbour would
    /// still take in, oldest first, with what is left of each success.
    #[must_use]
    pub fn capture_own_casts(&self, after: i64, now_ms: i64) -> Vec<PeerCast> {
        self.ring
            .iter()
            .filter(|e| e.sequence > after && now_ms - e.at_unix_ms <= STALE_AFTER_MS)
            .map(|e| project_cast(e.sequence, self.client_id, false, e, now_ms))
            .collect()
    }

    /// The lines this client broadcast above `after` that a neighbour would
    /// still take in, oldest first.
    #[must_use]
    pub fn capture_own_commands(&self, after: i64, now_ms: i64) -> Vec<PeerCommand> {
        self.command_ring
            .iter()
            .filter(|e| e.sequence > after && now_ms - e.at_unix_ms <= STALE_AFTER_MS)
            .map(|e| PeerCommand {
                sequence: e.sequence,
                client_id: self.client_id,
                sender_id: e.sender_id,
                tags: normalize_tags(&e.tags),
                line: e.line.clone(),
                sent_unix_ms: e.at_unix_ms,
                is_remote: false,
            })
            .collect()
    }

    /// Adds or refreshes a peer a plugin carried here from somewhere this
    /// client cannot read, stamped with this client's clock. False for a
    /// peer a note would not be accepted for either, for `own_player`, and
    /// while the import cap is full of peers that are still recent.
    pub fn import_remote_client(
        &mut self,
        client: &NetworkClient,
        own_player: u32,
        now_ms: i64,
    ) -> bool {
        let p = &client.position;
        let acceptable = client.player_id != 0
            && client.player_id != own_player
            && !client.name.trim().is_empty()
            && client.name.encode_utf16().count() <= MAXIMUM_NAME_LENGTH
            && client.world_name.encode_utf16().count() <= MAXIMUM_NAME_LENGTH
            && p.east_west.is_finite()
            && p.north_south.is_finite()
            && p.elevation.is_finite()
            && client.heading.is_finite()
            && client.tags.len() <= MAXIMUM_CLIENT_TAGS
            // A label too long to travel is refused rather than cut short.
            && client
                .tags
                .iter()
                .all(|t| t.trim().encode_utf16().count() <= MAXIMUM_TAG_LENGTH);
        if !acceptable {
            return false;
        }
        if !self.remote_peers.contains_key(&client.player_id) {
            if self.remote_peers.len() >= MAXIMUM_REMOTE_CLIENTS {
                self.remote_peers
                    .retain(|_, remote| is_recent(&remote.document, now_ms));
                if self.remote_peers.len() >= MAXIMUM_REMOTE_CLIENTS {
                    return false;
                }
            }
            let (instance, client_id) = self.new_remote_identity();
            let remote = RemotePeer {
                document: Document {
                    instance,
                    updated_unix_ms: now_ms,
                    client: NetworkClient {
                        client_id,
                        is_remote: true,
                        ..NetworkClient::default()
                    },
                    casts: Vec::new(),
                    commands: Vec::new(),
                },
                path: PathBuf::from(format!("remote:{}", instance.compact())),
                cast_sequence: 0,
                command_sequence: 0,
                listed: false,
            };
            self.remote_peers.insert(client.player_id, remote);
        }
        let Some(remote) = self.remote_peers.get_mut(&client.player_id) else {
            return false;
        };
        let document = &mut remote.document;
        document.updated_unix_ms = now_ms;
        document.client = NetworkClient {
            client_id: document.client.client_id,
            tags: normalize_tags(&client.tags),
            is_remote: true,
            ..client.clone()
        };
        true
    }

    /// Adds a cast an imported peer made to that peer's ring, stamped with
    /// this client's clock, under the rule a cast in a note is read by.
    /// False when the caster is not an imported peer that is still recent,
    /// or the cast is not well formed.
    pub fn import_remote_cast(&mut self, cast: &LocalCast, now_ms: i64) -> bool {
        let Some(remote) = self
            .remote_peers
            .get_mut(&cast.caster_id)
            .filter(|r| is_recent(&r.document, now_ms))
        else {
            return false;
        };
        let entry = CastEntry {
            sequence: remote.cast_sequence + 1,
            at_unix_ms: now_ms,
            caster_id: cast.caster_id,
            target_id: cast.target_id,
            spell_id: cast.spell_id,
            effective_skill: cast.effective_skill,
            // An attempt carries no duration, whatever the relay said.
            duration_seconds: if cast.landed {
                cast.duration_seconds
            } else {
                0.0
            },
            landed: cast.landed,
        };
        if !cast_well_formed(&entry) {
            return false;
        }
        remote.cast_sequence = entry.sequence;
        remote.document.casts.push(entry);
        trim_front(&mut remote.document.casts, CAST_RING_CAPACITY);
        true
    }

    /// Adds a line an imported peer broadcast to that peer's ring, stamped
    /// with this client's clock, under the rule a line in a note is read
    /// by. False when the sender is not an imported peer that is still
    /// recent, or the line is not well formed.
    pub fn import_remote_command(&mut self, command: &LocalCommand, now_ms: i64) -> bool {
        let Some(remote) = self
            .remote_peers
            .get_mut(&command.sender_id)
            .filter(|r| is_recent(&r.document, now_ms))
        else {
            return false;
        };
        let entry = CommandEntry {
            sequence: remote.command_sequence + 1,
            at_unix_ms: now_ms,
            sender_id: command.sender_id,
            tags: normalize_tags(&command.tags),
            line: command.line.clone(),
            delay_ms: command.delay_ms,
        };
        if !command_well_formed(&entry) || command.tags.len() > MAXIMUM_COMMAND_TAGS {
            return false;
        }
        remote.command_sequence = entry.sequence;
        remote.document.commands.push(entry);
        trim_front(&mut remote.document.commands, COMMAND_RING_CAPACITY);
        true
    }

    /// A fresh identity for an imported peer: an instance id no note
    /// carries and a client id drawn the way a client draws its own (the
    /// id's first field), never zero and never this client's.
    fn new_remote_identity(&mut self) -> (InstanceId, u32) {
        let seed = self
            .instance
            .0
            .iter()
            .fold(0u64, |h, b| h.rotate_left(8) ^ u64::from(*b));
        loop {
            self.remote_identities += 1;
            let a = split_mix(seed ^ self.remote_identities.wrapping_mul(0x9E37_79B9_7F4A_7C15));
            let b = split_mix(a);
            let mut bytes = [0u8; 16];
            bytes[..8].copy_from_slice(&a.to_be_bytes());
            bytes[8..].copy_from_slice(&b.to_be_bytes());
            let client_id = u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]);
            if client_id != 0 && client_id != self.client_id {
                return (InstanceId(bytes), client_id);
            }
        }
    }

    /// Drops every cast and line this client announced, for a character
    /// that has left the world: what the next character is read back as
    /// saying starts empty. The numbering carries on, so a neighbour's mark
    /// on this client stays valid.
    pub fn forget_own_announcements(&mut self) {
        self.ring.clear();
        self.command_ring.clear();
        self.cast_write_pending = false;
        self.command_write_pending = false;
    }

    /// The notes in the folder and the peers plugins imported. A character
    /// a recent note in the folder already speaks for, in the same world,
    /// is read from there: arriving by two routes it would be listed twice
    /// and its casts counted twice. What the hidden copy is handed meanwhile
    /// is passed over as it arrives, so nothing it carried is taken in when
    /// the note goes and the copy shows again; and a note that first shows
    /// while a copy of the same character was listed starts at the end of
    /// its rings, because the copy already carried what they hold.
    fn read_notes(&mut self, now_ms: i64) -> Vec<(PathBuf, Document)> {
        let mut notes = self.read_folder(now_ms);
        if self.remote_peers.is_empty() {
            return notes;
        }
        let local = notes.len();
        for remote in self.remote_peers.values_mut() {
            let shadowing = notes[..local].iter().position(|(_, d)| {
                d.client.player_id == remote.document.client.player_id
                    && is_recent(d, now_ms)
                    && same_world(d, &remote.document.client.world_name)
            });
            let Some(shadowing) = shadowing else {
                notes.push((remote.path.clone(), remote.document.clone()));
                remote.listed = true;
                continue;
            };
            // The hidden copy: everything it holds counts as read.
            self.observed_peers.insert(
                (remote.path.clone(), remote.document.instance),
                ObservedPeer {
                    highest_cast: remote.cast_sequence,
                    highest_command: remote.command_sequence,
                    last_seen_ms: now_ms,
                },
            );
            let (path, note) = &notes[shadowing];
            let note_cursor = (path.clone(), note.instance);
            if remote.listed
                && is_recent(&remote.document, now_ms)
                && !self.observed_peers.contains_key(&note_cursor)
            {
                self.observed_peers.insert(
                    note_cursor,
                    ObservedPeer {
                        highest_cast: note.casts.iter().map(|c| c.sequence).max().unwrap_or(0),
                        highest_command: note
                            .commands
                            .iter()
                            .map(|c| c.sequence)
                            .max()
                            .unwrap_or(0),
                        last_seen_ms: now_ms,
                    },
                );
            }
            remote.listed = false;
        }
        notes
    }

    /// Takes this client's note away (it left the world or stopped).
    pub fn withdraw(&mut self) {
        if self.memory_only {
            return;
        }
        self.cast_write_pending = false;
        self.command_write_pending = false;
        // Nothing to do when it is already gone.
        let _ = fs::remove_file(&self.path);
    }

    /// The hub says who another client is now (OpenAC `PushClient`): its
    /// note is replaced, keeping the casts it already carried. Ignored for
    /// this client, no character, a position that is not a number, or a
    /// 257th client.
    pub fn push_client(&mut self, instance: InstanceId, client: &NetworkClient, now_ms: i64) {
        let p = &client.position;
        let writable = p.east_west.is_finite()
            && p.north_south.is_finite()
            && p.elevation.is_finite()
            && client.heading.is_finite();
        if instance.is_empty() || instance == self.instance || client.player_id == 0 || !writable {
            return;
        }
        let at = self.pushed.iter().position(|(i, _)| *i == instance);
        if at.is_none() && self.pushed.len() >= MAXIMUM_REMOTE_CLIENTS {
            return;
        }
        let casts = at.map_or_else(Vec::new, |at| self.pushed[at].1.casts.clone());
        let document = Document {
            instance,
            updated_unix_ms: now_ms,
            client: NetworkClient {
                is_remote: false,
                tags: normalize_tags(&client.tags),
                ..client.clone()
            },
            casts,
            commands: Vec::new(),
        };
        match at {
            Some(at) => self.pushed[at].1 = document,
            None => self.pushed.push((instance, document)),
        }
    }

    /// A cast the hub forwarded from a pushed client (OpenAC `PushCast`),
    /// stamped `at_unix_ms` as the hub saw it go by; one that is not well
    /// formed is dropped.
    pub fn push_cast(&mut self, instance: InstanceId, cast: &PeerCast, at_unix_ms: i64) {
        let Some((_, note)) = self.pushed.iter_mut().find(|(i, _)| *i == instance) else {
            return;
        };
        let entry = CastEntry {
            sequence: cast.sequence,
            at_unix_ms,
            caster_id: cast.caster_id,
            target_id: cast.target_id,
            spell_id: cast.spell_id,
            effective_skill: cast.effective_skill,
            duration_seconds: cast.seconds_remaining,
            landed: cast.landed,
        };
        if !cast_well_formed(&entry) {
            return;
        }
        note.casts.push(entry);
        trim_front(&mut note.casts, CAST_RING_CAPACITY);
    }

    /// A line the hub delivered to this client (OpenAC `PushCommand`),
    /// with the stagger the hub worked out for it: read back by
    /// [`Self::remote_commands`] like a neighbour's line. Its sequence.
    pub fn push_command(
        &mut self,
        sender: &NetworkClient,
        line: &str,
        tags: &[String],
        delay_ms: i32,
        sent_unix_ms: i64,
    ) -> i64 {
        self.observed_command_sequence += 1;
        self.observed_commands.push(ObservedCommand {
            sequence: self.observed_command_sequence,
            client_id: sender.client_id,
            is_remote: false,
            entry: CommandEntry {
                sequence: 0,
                at_unix_ms: sent_unix_ms,
                sender_id: sender.player_id,
                tags: tags.to_vec(),
                line: line.to_owned(),
                delay_ms,
            },
            aimed_at: tags.to_vec(),
            stagger_ms: delay_ms,
        });
        trim_front(&mut self.observed_commands, OBSERVED_COMMAND_CAPACITY);
        self.observed_command_sequence
    }

    /// The hub says a client left (OpenAC `RemovePushedClient`): its note
    /// goes, and the casts read from it with it.
    pub fn remove_pushed_client(&mut self, instance: InstanceId) {
        if let Some(at) = self.pushed.iter().position(|(i, _)| *i == instance) {
            let (_, note) = self.pushed.remove(at);
            let client_id = note.client.client_id;
            self.observed_casts
                .retain(|cast| cast.is_remote || cast.client_id != client_id);
        }
    }

    /// The hub connection ended or sharing stopped (OpenAC
    /// `ClearPushedClients`): every pushed client goes, and every cast
    /// read from this computer's clients.
    pub fn clear_pushed_clients(&mut self) {
        self.pushed.clear();
        self.observed_casts.retain(|cast| cast.is_remote);
    }

    fn forget_stale(&mut self, now_ms: i64) {
        self.observed_casts
            .retain(|o| now_ms - o.entry.at_unix_ms <= STALE_AFTER_MS);
        self.observed_commands
            .retain(|o| now_ms - o.entry.at_unix_ms <= STALE_AFTER_MS);
        self.observed_peers
            .retain(|_, p| now_ms - p.last_seen_ms <= STALE_AFTER_MS * 4);
    }

    /// Every other client's note that is present and well shaped.
    fn read_folder(&self, now_ms: i64) -> Vec<(PathBuf, Document)> {
        if self.memory_only {
            // A pushed client is present until the hub says it left.
            return self
                .pushed
                .iter()
                .map(|(instance, document)| {
                    let mut document = document.clone();
                    document.updated_unix_ms = now_ms;
                    (pushed_path(*instance), document)
                })
                .collect();
        }
        let Ok(entries) = fs::read_dir(&self.directory) else {
            return Vec::new();
        };
        let mut notes = Vec::new();
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().to_lowercase();
            if !name.starts_with("peer-") || !name.ends_with(".json") || path == self.path {
                continue;
            }
            let Ok(meta) = entry.metadata() else {
                continue;
            };
            if meta.len() == 0 || meta.len() > MAXIMUM_DOCUMENT_BYTES {
                continue;
            }
            // A peer may replace or remove its note between the listing and
            // the read; it is read again next time.
            let Ok(bytes) = fs::read(&path) else {
                continue;
            };
            if let Some(document) = read_document(&bytes).and_then(|d| self.accept(d)) {
                notes.push((path, document));
            }
        }
        notes
    }

    /// A note this client would believe (OpenAC `Accept`): written by
    /// somebody else, naming a character standing somewhere; a ring with
    /// one bad row is dropped on its own.
    fn accept(&self, mut d: Document) -> Option<Document> {
        let c = &d.client;
        let acceptable = d.instance != self.instance
            && !d.instance.is_empty()
            && c.client_id != 0
            && c.player_id != 0
            && !c.name.trim().is_empty()
            && c.name.encode_utf16().count() <= 128
            && c.world_name.encode_utf16().count() <= 128
            && c.tags.len() <= 128
            && tags_well_formed(&c.tags)
            && c.position.east_west.is_finite()
            && c.position.north_south.is_finite()
            && c.position.elevation.is_finite()
            && c.heading.is_finite()
            && d.casts.len() <= CAST_RING_CAPACITY
            && d.commands.len() <= COMMAND_RING_CAPACITY;
        if !acceptable {
            return None;
        }
        d.client.tags = normalize_tags(&d.client.tags);
        if !d.casts.iter().all(cast_well_formed) {
            d.casts.clear();
        }
        if !d.commands.iter().all(command_well_formed) {
            d.commands.clear();
        }
        Some(d)
    }
}

/// The key a pushed client's note is read under: no file has it.
fn pushed_path(instance: InstanceId) -> PathBuf {
    PathBuf::from(format!("pushed:{}", instance.compact()))
}

/// [`HEARTBEAT_SECONDS`] in milliseconds.
const HEARTBEAT_MS: i64 = 5_000;

fn heartbeat_ms() -> i64 {
    HEARTBEAT_MS
}

/// One step of SplitMix64: spreads a counter into well-mixed bits.
fn split_mix(mut z: u64) -> u64 {
    z = z.wrapping_add(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

fn trim_front<T>(list: &mut Vec<T>, capacity: usize) {
    if list.len() > capacity {
        list.drain(..list.len() - capacity);
    }
}

// ── The note's JSON: OpenAC's `PeerDocument` as System.Text.Json writes it
// (member names as declared, read ignoring case). ─────────────────────────

fn write_document(d: &Document) -> String {
    use std::fmt::Write as _;
    let c = &d.client;
    let mut out = String::from("{\"InstanceId\":");
    json::write_string(&mut out, &d.instance.text());
    let _ = write!(
        out,
        ",\"UpdatedUnixMs\":{},\"ClientId\":{},\"PlayerId\":{},\"Name\":",
        d.updated_unix_ms, c.client_id, c.player_id
    );
    json::write_string(&mut out, &c.name);
    out.push_str(",\"WorldName\":");
    json::write_string(&mut out, &c.world_name);
    out.push_str(",\"Tags\":");
    write_strings(&mut out, &c.tags);
    let p = &c.position;
    let _ = write!(
        out,
        ",\"CellId\":{},\"EastWest\":{},\"NorthSouth\":{},\"Elevation\":{},\"IsOutdoor\":{},\"Heading\":{},\
         \"CurrentHealth\":{},\"CurrentMana\":{},\"CurrentStamina\":{},\"MaxHealth\":{},\"MaxMana\":{},\"MaxStamina\":{},\"Casts\":[",
        p.cell_id,
        p.east_west,
        p.north_south,
        p.elevation,
        p.is_outdoor,
        c.heading,
        c.current_health,
        c.current_mana,
        c.current_stamina,
        c.max_health,
        c.max_mana,
        c.max_stamina
    );
    for (i, e) in d.casts.iter().enumerate() {
        if i > 0 {
            out.push(',');
        }
        let _ = write!(
            out,
            "{{\"Sequence\":{},\"AtUnixMs\":{},\"CasterObjectId\":{},\"TargetObjectId\":{},\"SpellId\":{},\
             \"EffectiveSkill\":{},\"DurationSeconds\":{},\"Landed\":{}}}",
            e.sequence,
            e.at_unix_ms,
            e.caster_id,
            e.target_id,
            e.spell_id,
            e.effective_skill,
            e.duration_seconds,
            e.landed
        );
    }
    out.push_str("],\"Commands\":[");
    for (i, e) in d.commands.iter().enumerate() {
        if i > 0 {
            out.push(',');
        }
        let _ = write!(
            out,
            "{{\"Sequence\":{},\"AtUnixMs\":{},\"SenderObjectId\":{},\"Tags\":",
            e.sequence, e.at_unix_ms, e.sender_id
        );
        write_strings(&mut out, &e.tags);
        out.push_str(",\"Line\":");
        json::write_string(&mut out, &e.line);
        let _ = write!(out, ",\"DelayMilliseconds\":{}}}", e.delay_ms);
    }
    out.push_str("]}");
    out
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

/// An object's members by name, ignoring case (the last wins).
pub(super) struct Members<'a>(&'a [json::Member]);

impl<'a> Members<'a> {
    pub(super) fn get(&self, name: &str) -> Option<&'a Node> {
        self.0
            .iter()
            .rev()
            .find(|m| m.name.eq_ignore_ascii_case(name))
            .map(|m| &m.value)
    }

    /// A number member parsed as `T`; a missing member is the default; a
    /// wrong type or a number out of range fails the note.
    pub(super) fn number<T: std::str::FromStr + Default>(&self, name: &str) -> Option<T> {
        match self.get(name).map(|n| &n.value) {
            None => Some(T::default()),
            Some(Value::Number(text)) => text.parse().ok(),
            Some(_) => None,
        }
    }

    pub(super) fn boolean(&self, name: &str) -> Option<bool> {
        match self.get(name).map(|n| &n.value) {
            None => Some(false),
            Some(Value::Bool(b)) => Some(*b),
            Some(_) => None,
        }
    }

    /// A string member; `null` reads as none (which the caller refuses
    /// where the note needs one).
    pub(super) fn string(&self, name: &str) -> Option<Option<String>> {
        match self.get(name).map(|n| &n.value) {
            None => Some(Some(String::new())),
            Some(Value::Null) => Some(None),
            Some(Value::String(s)) => Some(Some(s.clone())),
            Some(_) => None,
        }
    }

    /// A string array; `null` (or a `null` element) reads as none.
    pub(super) fn strings(&self, name: &str) -> Option<Option<Vec<String>>> {
        match self.get(name).map(|n| &n.value) {
            None => Some(Some(Vec::new())),
            Some(Value::Null) => Some(None),
            Some(Value::Array(items)) => {
                let mut out = Vec::new();
                for item in items {
                    match &item.value {
                        Value::String(s) => out.push(s.clone()),
                        Value::Null => return Some(None),
                        _ => return None,
                    }
                }
                Some(Some(out))
            }
            Some(_) => None,
        }
    }

    pub(super) fn items(&self, name: &str) -> Option<Option<&'a [Node]>> {
        match self.get(name).map(|n| &n.value) {
            None => Some(Some(&[])),
            Some(Value::Null) => Some(None),
            Some(Value::Array(items)) => Some(Some(items.as_slice())),
            Some(_) => None,
        }
    }
}

pub(super) fn object(node: &Node) -> Option<Members<'_>> {
    match &node.value {
        Value::Object(members) => Some(Members(members)),
        _ => None,
    }
}

/// A note as written, or none for bytes that are not one. A `null` where
/// the note needs a value makes it unbelievable; a `null` row in a ring
/// makes that ring unbelievable (it is dropped by [`PeerRegistry::accept`]
/// through an impossible row).
fn read_document(bytes: &[u8]) -> Option<Document> {
    let root = json::parse(bytes).ok()?;
    let m = object(&root)?;
    let instance = InstanceId::parse(&m.string("InstanceId")??)?;
    let tags = m.strings("Tags")??;
    let name = m.string("Name")??;
    let world_name = m.string("WorldName")??;
    let heading: f32 = m.number("Heading")?;
    let client = NetworkClient {
        client_id: m.number("ClientId")?,
        player_id: m.number("PlayerId")?,
        name,
        world_name,
        position: Position {
            cell_id: m.number("CellId")?,
            east_west: m.number("EastWest")?,
            north_south: m.number("NorthSouth")?,
            elevation: m.number("Elevation")?,
            heading_degrees: heading,
            is_outdoor: m.boolean("IsOutdoor")?,
        },
        tags,
        current_health: m.number("CurrentHealth")?,
        current_mana: m.number("CurrentMana")?,
        current_stamina: m.number("CurrentStamina")?,
        max_health: m.number("MaxHealth")?,
        max_mana: m.number("MaxMana")?,
        max_stamina: m.number("MaxStamina")?,
        heading,
        is_remote: false,
    };
    let mut casts = Vec::new();
    for item in m.items("Casts")?? {
        casts.push(match object(item) {
            Some(c) => CastEntry {
                sequence: c.number("Sequence")?,
                at_unix_ms: c.number("AtUnixMs")?,
                caster_id: c.number("CasterObjectId")?,
                target_id: c.number("TargetObjectId")?,
                spell_id: c.number("SpellId")?,
                effective_skill: c.number("EffectiveSkill")?,
                duration_seconds: c.number("DurationSeconds")?,
                landed: c.boolean("Landed")?,
            },
            // A null row: impossible, so the ring is dropped.
            None if matches!(item.value, Value::Null) => impossible_cast(),
            None => return None,
        });
    }
    let mut commands = Vec::new();
    for item in m.items("Commands")?? {
        commands.push(match object(item) {
            Some(c) => CommandEntry {
                sequence: c.number("Sequence")?,
                at_unix_ms: c.number("AtUnixMs")?,
                sender_id: c.number("SenderObjectId")?,
                // A null label list or label: impossible.
                tags: c.strings("Tags")?.unwrap_or_else(|| vec![String::new()]),
                line: c.string("Line")?.unwrap_or_default(),
                delay_ms: c.number("DelayMilliseconds")?,
            },
            None if matches!(item.value, Value::Null) => impossible_command(),
            None => return None,
        });
    }
    Some(Document {
        instance,
        updated_unix_ms: m.number("UpdatedUnixMs")?,
        client,
        casts,
        commands,
    })
}

fn impossible_cast() -> CastEntry {
    CastEntry {
        sequence: 0,
        at_unix_ms: 0,
        caster_id: 0,
        target_id: 0,
        spell_id: 0,
        effective_skill: -1,
        duration_seconds: -1.0,
        landed: false,
    }
}

fn impossible_command() -> CommandEntry {
    CommandEntry {
        sequence: 0,
        at_unix_ms: 0,
        sender_id: 0,
        tags: Vec::new(),
        line: String::new(),
        delay_ms: -1,
    }
}
