//! API group M, the peers half (OpenAC `RuntimeAutomationSurface`'s
//! `INetworkAutomation` and its peer tick): this client's labels, its own
//! record, the casts and lines it announces, and the lines other clients
//! broadcast to it, run by the client itself through its chat entry once
//! each one's stagger is up.
//!
//! A session talks to the other clients on this computer through the peer
//! hub ([`super::peer_transport`], OpenAC's production path): nothing moves
//! until a plugin subscribes, and what the hub pushes lands in a registry
//! kept in memory. The shared-folder notes (OpenAC's registry without a
//! transport) remain for the registry's own tests and run when a session is
//! set up without a hub.

use std::path::Path;

use ac_core::player::VitalKind;
use ac_plugin::abi::CharacterInfo;
use ac_plugin::abi::peers::{
    Broadcast, CastAnnouncement, NetworkClient, PeerCast, PeerCommand, RemoteBroadcast, RemoteCast,
};

use super::PluginBinding;
use super::peer_leases::{CASTS, COMMANDS, PeerHub, PeerLeases};
use super::peer_transport::{PeerTransport, PushedCommand, StartHub};
use super::peers::{
    COMMAND_POLL_SECONDS, HEARTBEAT_SECONDS, InstanceId, LocalCast, LocalCommand,
    MAXIMUM_TAG_LENGTH, OBSERVED_COMMAND_CAPACITY, PeerRegistry,
};
use crate::session::SessionState;

/// How often this client's state goes to the hub while somebody wants it,
/// seconds.
const STATE_SECONDS: f64 = 0.3;
/// How many lines taken from the hub may wait for their stagger.
const PENDING_PUSHED_CAPACITY: usize = 256;

/// This client's part in the peer registry.
#[derive(Debug, Default)]
pub struct PeerLink {
    registry: Option<PeerRegistry>,
    /// The peer hub connection (none: the folder notes, without a hub).
    transport: Option<PeerTransport>,
    tags: Vec<String>,
    heartbeat_remaining: f64,
    poll_remaining: f64,
    state_remaining: f64,
    delivered_sequence: i64,
    /// Lines taken from others: (due, UTC ms; the line).
    pending: Vec<(i64, String)>,
    /// The character was in the world at the last tick.
    was_in_world: bool,
    /// The peer subscriptions the session's plugins hold.
    pub(super) leases: PeerLeases,
}

/// The labels a client answers to (OpenAC `NormalizePeerTags`): trimmed,
/// blanks and over-long ones dropped, repeats (any case) folded, at most
/// 128.
#[must_use]
pub fn normalize_peer_tags(tags: &[String]) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    for tag in tags {
        let tag = tag.trim();
        if tag.is_empty()
            || tag.encode_utf16().count() > MAXIMUM_TAG_LENGTH
            || out.iter().any(|t| t.eq_ignore_ascii_case(tag))
        {
            continue;
        }
        out.push(tag.to_owned());
        if out.len() == 128 {
            break;
        }
    }
    out
}

/// A fresh instance id from the clock and the process (no two clients on a
/// computer start in the same nanosecond in the same process).
fn new_instance(seed: u64) -> InstanceId {
    let mix = |mut z: u64| {
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    };
    let a = mix(seed);
    let b = mix(a ^ u64::from(std::process::id()));
    let mut bytes = [0u8; 16];
    bytes[..8].copy_from_slice(&a.to_be_bytes());
    bytes[8..].copy_from_slice(&b.to_be_bytes());
    InstanceId(bytes)
}

/// Leases from the session's hub connection.
struct LinkHub<'a> {
    transport: &'a mut PeerTransport,
    registry: &'a mut PeerRegistry,
}

impl PeerHub for LinkHub<'_> {
    fn subscribe(&mut self, capabilities: u32) -> Option<u64> {
        self.transport.subscribe(capabilities, self.registry)
    }

    fn release(&mut self, lease: u64) {
        self.transport.release(lease, self.registry);
    }
}

/// No hub: nothing is handed out.
struct NoHub;

impl PeerHub for NoHub {
    fn subscribe(&mut self, _capabilities: u32) -> Option<u64> {
        None
    }

    fn release(&mut self, _lease: u64) {}
}

impl PeerLink {
    /// Takes part in the peers of `directory` through its peer hub (OpenAC
    /// `RuntimeAutomationBindings.CreateSurface`: a registry in memory and
    /// the hub of that folder), answering to `tags` (the session document's
    /// `pluginTags`).
    pub fn configure(&mut self, directory: &Path, tags: &[String], seed: u64) {
        let instance = new_instance(seed);
        self.configure_with_hub(
            directory,
            instance,
            tags,
            super::peer_transport::endpoint_for(directory),
            super::peer_transport::start_installed_hub(directory.to_path_buf()),
        );
    }

    /// Takes part through the hub at `endpoint`, started with `start_hub`.
    pub(crate) fn configure_with_hub(
        &mut self,
        directory: &Path,
        instance: InstanceId,
        tags: &[String],
        endpoint: ac_plugin_net::local::LocalEndpoint,
        start_hub: StartHub,
    ) {
        self.registry = Some(PeerRegistry::in_memory(directory, instance));
        self.transport = Some(PeerTransport::new(endpoint, instance, start_hub));
        self.tags = normalize_peer_tags(tags);
    }

    /// Part in the folder notes with a known instance and no hub (tests).
    #[cfg(test)]
    pub(crate) fn configure_as(&mut self, directory: &Path, instance: InstanceId, tags: &[String]) {
        self.registry = Some(PeerRegistry::new(directory, instance));
        self.transport = None;
        self.tags = normalize_peer_tags(tags);
    }

    /// The hub connection, when there is one (tests).
    #[cfg(test)]
    pub(crate) fn transport(&self) -> Option<&PeerTransport> {
        self.transport.as_ref()
    }

    /// The session ends: the hub connection closes and this client's note
    /// goes.
    pub fn withdraw(&mut self) {
        if let Some(registry) = self.registry.as_mut() {
            if let Some(transport) = self.transport.as_mut() {
                transport.dispose(registry);
            }
            registry.withdraw();
        }
    }

    /// Plugin `plugin` was unloaded: the leases it still holds go back.
    pub fn release_plugin(&mut self, plugin: &str) {
        match (self.transport.as_mut(), self.registry.as_mut()) {
            (Some(transport), Some(registry)) => self.leases.release_plugin(
                plugin,
                &mut LinkHub {
                    transport,
                    registry,
                },
            ),
            _ => self.leases.release_plugin(plugin, &mut NoHub),
        }
    }

    /// Lines a pushed command or an import added, in the queue at the
    /// instant this client owes each (OpenAC `TakeInPeerCommands`).
    fn take_in(&mut self, world: &str, player: u32, now_ms: i64) {
        let Some(registry) = self.registry.as_mut() else {
            return;
        };
        let taken =
            registry.remote_commands(self.delivered_sequence, world, player, &self.tags, now_ms);
        for remote in taken {
            self.delivered_sequence = self.delivered_sequence.max(remote.command.sequence);
            self.pending
                .push((now_ms + i64::from(remote.stagger_ms), remote.command.line));
        }
        if self.pending.len() > OBSERVED_COMMAND_CAPACITY {
            let excess = self.pending.len() - OBSERVED_COMMAND_CAPACITY;
            self.pending.drain(..excess);
        }
    }

    /// A line the hub delivered (OpenAC `ReceivePushedCommand`): false
    /// when the queue of waiting lines is full.
    fn receive_pushed(&mut self, command: PushedCommand, now_ms: i64) -> bool {
        if self.pending.len() >= PENDING_PUSHED_CAPACITY {
            return false;
        }
        let Some(registry) = self.registry.as_mut() else {
            return true;
        };
        let sequence = registry.push_command(
            &command.sender,
            &command.line,
            &command.tags,
            command.delay_ms,
            command.sent_unix_ms,
        );
        self.delivered_sequence = self.delivered_sequence.max(sequence);
        self.pending
            .push((now_ms + i64::from(command.delay_ms), command.line));
        true
    }

    /// The lines whose stagger is up, oldest first (OpenAC
    /// `RunDuePeerCommands`).
    fn take_due(&mut self, now_ms: i64) -> Vec<String> {
        let mut due = Vec::new();
        self.pending.retain(|(at, line)| {
            if *at <= now_ms {
                due.push(line.clone());
                false
            } else {
                true
            }
        });
        due
    }
}

/// This client as it tells the others (OpenAC `TryBuildOwnPeerClient`):
/// none while the character is not in the world, or, with a hub, while no
/// plugin takes part in anything.
fn me(
    state: &mut SessionState,
    character: Option<&CharacterInfo>,
    in_world: bool,
    client_id: u32,
    tags: &[String],
) -> Option<NetworkClient> {
    if state
        .plugin_services
        .peers
        .transport
        .as_ref()
        .is_some_and(|t| !t.is_active())
    {
        return None;
    }
    let character = character.filter(|c| c.id != 0)?;
    let position = super::navigation::live_position(state, in_world)?;
    let vital = |kind: VitalKind| {
        state.world.player().map_or((0, 0), |p| {
            let numbers = p.numbers();
            (
                numbers.get(kind).map_or(0, |v| v.current),
                numbers.get_max_approx(kind).unwrap_or(0),
            )
        })
    };
    let (health, max_health) = vital(VitalKind::Health);
    let (mana, max_mana) = vital(VitalKind::Mana);
    let (stamina, max_stamina) = vital(VitalKind::Stamina);
    Some(NetworkClient {
        client_id,
        player_id: character.id,
        name: character.name.clone(),
        world_name: character.world.clone(),
        heading: position.heading_degrees,
        position,
        tags: tags.to_vec(),
        current_health: health,
        current_mana: mana,
        current_stamina: stamina,
        max_health,
        max_mana,
        max_stamina,
        is_remote: false,
    })
}

/// Tells the others who this client is (OpenAC `PublishPeerSnapshot`):
/// the hub gets its record, or the note is rewritten; out of the world (or
/// with nothing shared) it withdraws.
fn publish(
    state: &mut SessionState,
    character: Option<&CharacterInfo>,
    in_world: bool,
    now_ms: i64,
) {
    let Some(registry) = state.plugin_services.peers.registry.as_ref() else {
        return;
    };
    let client_id = registry.client_id();
    let tags = state.plugin_services.peers.tags.clone();
    let client = me(state, character, in_world, client_id, &tags);
    let link = &mut state.plugin_services.peers;
    let Some(registry) = link.registry.as_mut() else {
        return;
    };
    match (client, link.transport.as_mut()) {
        (Some(client), Some(transport)) => transport.update_self(client, registry),
        (Some(client), None) => {
            registry.publish(&client, now_ms);
        }
        (None, transport) => {
            if let Some(transport) = transport {
                transport.withdraw(registry);
            }
            registry.withdraw();
        }
    }
}

/// Hands the hub connection's lines to the host as the peers' log.
fn report(state: &mut SessionState) {
    let Some(reports) = state
        .plugin_services
        .peers
        .transport
        .as_mut()
        .map(PeerTransport::take_reports)
    else {
        return;
    };
    for message in reports {
        state.push_host_event(crate::session::HostEvent::Plugin(super::PluginEvent::Log {
            plugin: ac_plugin::session::MANAGER_ID.to_owned(),
            level: ac_plugin::abi::LogLevel::Warn,
            message,
        }));
    }
}

/// One plugin tick (OpenAC `OnPeerTick`), returning the lines others
/// broadcast here whose stagger is up (oldest first) for the session to
/// submit. With the hub: what it pushed is taken in, and while somebody
/// wants this client's state it is sent every 0.3 s. Without: the lines
/// are taken in on their poll and the note is rewritten on the heartbeat,
/// or sooner for a cast or line waiting.
pub fn tick(
    state: &mut SessionState,
    character: Option<&CharacterInfo>,
    in_world: bool,
    elapsed_seconds: f64,
    now_ms: i64,
) -> Vec<String> {
    let link = &mut state.plugin_services.peers;
    let Some(registry) = link.registry.as_mut() else {
        return Vec::new();
    };
    // What the character that left announced is not what the next one
    // says, and the hub hears it left.
    if link.was_in_world && !in_world {
        registry.forget_own_announcements();
        if let Some(transport) = link.transport.as_mut() {
            transport.withdraw(registry);
        }
    }
    link.was_in_world = in_world;
    if link.transport.is_some() {
        let due = hub_tick(state, character, in_world, elapsed_seconds, now_ms);
        report(state);
        return due;
    }
    link.poll_remaining -= elapsed_seconds.max(0.0);
    if link.poll_remaining <= 0.0 {
        link.poll_remaining = COMMAND_POLL_SECONDS;
        if let Some(character) = character.filter(|c| c.id != 0) {
            link.take_in(&character.world, character.id, now_ms);
        }
    }
    let due = link.take_due(now_ms);
    link.heartbeat_remaining -= elapsed_seconds.max(0.0);
    let write_due = link
        .registry
        .as_ref()
        .is_some_and(|r| r.is_cast_write_due(now_ms) || r.is_command_write_due(now_ms));
    if link.heartbeat_remaining <= 0.0 || write_due {
        link.heartbeat_remaining = HEARTBEAT_SECONDS;
        publish(state, character, in_world, now_ms);
    }
    due
}

/// [`tick`] with the hub.
fn hub_tick(
    state: &mut SessionState,
    character: Option<&CharacterInfo>,
    in_world: bool,
    elapsed_seconds: f64,
    now_ms: i64,
) -> Vec<String> {
    let link = &mut state.plugin_services.peers;
    let (Some(transport), Some(registry)) = (link.transport.as_mut(), link.registry.as_mut())
    else {
        return Vec::new();
    };
    let pushed = transport.pump(registry, now_ms);
    for command in pushed {
        if !link.receive_pushed(command, now_ms) {
            if let Some(transport) = link.transport.as_mut() {
                transport.close_connection("Incoming peer command queue is full.");
            }
            break;
        }
    }
    let Some(transport) = link.transport.as_ref() else {
        return Vec::new();
    };
    if !transport.is_active() {
        return Vec::new();
    }
    if !transport.has(COMMANDS) {
        link.pending.clear();
    }
    let due = link.take_due(now_ms);
    link.state_remaining -= elapsed_seconds.max(0.0);
    let wanted = link
        .transport
        .as_ref()
        .is_some_and(|t| !t.has_self() || t.needs_state());
    if link.state_remaining <= 0.0 && wanted {
        link.state_remaining = STATE_SECONDS;
        publish(state, character, in_world, now_ms);
    }
    due
}

impl PluginBinding<'_> {
    fn peers_now(&self) -> i64 {
        self.wall_unix_ms
    }

    pub(super) fn peers_clients(&mut self) -> Vec<NetworkClient> {
        let now = self.peers_now();
        let own = self.own_character_id();
        self.state
            .plugin_services
            .peers
            .registry
            .as_mut()
            .map_or_else(Vec::new, |r| r.remote_clients(own, now))
    }

    fn own_character_id(&self) -> u32 {
        self.character.as_ref().map_or(0, |c| c.id)
    }

    /// Whether peers are read and written (OpenAC `IsConnected`): with the
    /// hub, once this client registered and heard back; without, while peer
    /// communication is on.
    pub(super) fn peers_is_connected(&self) -> bool {
        let link = &self.state.plugin_services.peers;
        link.registry.is_some()
            && link
                .transport
                .as_ref()
                .is_none_or(PeerTransport::is_connected)
    }

    /// Plugin `plugin` takes part in the peer features `capabilities`
    /// names (OpenAC `Subscribe`): its lease; none while peer communication
    /// is off, or without a hub.
    pub(super) fn peers_subscribe(&mut self, plugin: &str, capabilities: u32) -> Option<u32> {
        let PeerLink {
            registry,
            transport,
            leases,
            ..
        } = &mut self.state.plugin_services.peers;
        let (Some(registry), Some(transport)) = (registry.as_mut(), transport.as_mut()) else {
            return None;
        };
        leases.subscribe(
            plugin,
            capabilities,
            &mut LinkHub {
                transport,
                registry,
            },
        )
    }

    /// Plugin `plugin` gives back a lease (OpenAC disposing the lease
    /// `Subscribe` returned): false for one it does not hold.
    pub(super) fn peers_unsubscribe(&mut self, plugin: &str, handle: u32) -> bool {
        let PeerLink {
            registry,
            transport,
            leases,
            ..
        } = &mut self.state.plugin_services.peers;
        match (registry.as_mut(), transport.as_mut()) {
            (Some(registry), Some(transport)) => leases.unsubscribe(
                plugin,
                handle,
                &mut LinkHub {
                    transport,
                    registry,
                },
            ),
            _ => leases.unsubscribe(plugin, handle, &mut NoHub),
        }
    }

    /// This client's own record as it would tell it now (OpenAC
    /// `TryCaptureSelf`).
    pub(super) fn peers_capture_self(&mut self) -> Option<NetworkClient> {
        let client_id = self
            .state
            .plugin_services
            .peers
            .registry
            .as_ref()?
            .client_id();
        let tags = self.state.plugin_services.peers.tags.clone();
        let in_world = self.in_world();
        let character = self.character.clone();
        me(self.state, character.as_ref(), in_world, client_id, &tags)
    }

    /// The casts this client announced, for a relay (OpenAC
    /// `CaptureOwnCasts`).
    pub(super) fn peers_own_casts(&self, after: i64) -> Vec<PeerCast> {
        let now = self.peers_now();
        self.state
            .plugin_services
            .peers
            .registry
            .as_ref()
            .map_or_else(Vec::new, |r| r.capture_own_casts(after, now))
    }

    /// The lines this client broadcast, for a relay (OpenAC
    /// `CaptureOwnCommands`).
    pub(super) fn peers_own_commands(&self, after: i64) -> Vec<PeerCommand> {
        let now = self.peers_now();
        self.state
            .plugin_services
            .peers
            .registry
            .as_ref()
            .map_or_else(Vec::new, |r| r.capture_own_commands(after, now))
    }

    /// A peer from elsewhere (OpenAC `ImportRemoteClient`): only while this
    /// character is in the world, to tell the import from it.
    pub(super) fn peers_import_client(&mut self, client: &NetworkClient) -> bool {
        let own = self.own_character_id();
        if own == 0 {
            return false;
        }
        let now = self.peers_now();
        self.state
            .plugin_services
            .peers
            .registry
            .as_mut()
            .is_some_and(|r| r.import_remote_client(client, own, now))
    }

    /// A cast an imported peer made (OpenAC `ImportRemoteCast`): classified
    /// in this client's own spell table, as a neighbour's cast is.
    pub(super) fn peers_import_cast(&mut self, cast: &RemoteCast) -> bool {
        if self.own_character_id() == 0 {
            return false;
        }
        let known = self
            .state
            .world
            .spellbook()
            .is_some_and(|b| b.try_get_metadata(cast.spell_id).is_some());
        if !known {
            return false;
        }
        let now = self.peers_now();
        self.state
            .plugin_services
            .peers
            .registry
            .as_mut()
            .is_some_and(|r| {
                r.import_remote_cast(
                    &LocalCast {
                        caster_id: cast.caster_id,
                        target_id: cast.target_id,
                        spell_id: cast.spell_id,
                        effective_skill: cast.effective_skill,
                        duration_seconds: cast.seconds_remaining,
                        landed: cast.landed,
                    },
                    now,
                )
            })
    }

    /// A line an imported peer broadcast (OpenAC `ImportRemoteCommand`):
    /// with the hub, taken in at once when this client receives lines;
    /// without, by the next poll.
    pub(super) fn peers_import_command(&mut self, command: RemoteBroadcast) -> bool {
        let Some(character) = self.character.clone().filter(|c| c.id != 0) else {
            return false;
        };
        let now = self.peers_now();
        let link = &mut self.state.plugin_services.peers;
        let accepted = link.registry.as_mut().is_some_and(|r| {
            r.import_remote_command(
                &LocalCommand {
                    sender_id: command.sender_id,
                    tags: command.tags,
                    line: command.line,
                    delay_ms: command.delay_ms,
                },
                now,
            )
        });
        if accepted && link.transport.as_ref().is_some_and(|t| t.has(COMMANDS)) {
            link.take_in(&character.world, character.id, now);
        }
        accepted
    }

    /// Announces a cast (OpenAC `AnnounceCast`): only for a spell this
    /// client's table names, from the character in the world; with the hub,
    /// only while a plugin shares casts, and sent at once.
    pub(super) fn peers_announce_cast(&mut self, cast: &CastAnnouncement) -> bool {
        let Some(caster) = self.character.as_ref().map(|c| c.id).filter(|id| *id != 0) else {
            return false;
        };
        let known = self
            .state
            .world
            .spellbook()
            .is_some_and(|b| b.try_get_metadata(cast.spell_id).is_some());
        if !known {
            return false;
        }
        let now = self.peers_now();
        let link = &mut self.state.plugin_services.peers;
        if link.transport.as_ref().is_some_and(|t| !t.has(CASTS)) {
            return false;
        }
        let Some(registry) = link.registry.as_mut() else {
            return false;
        };
        let recorded = registry.record_cast(
            &LocalCast {
                caster_id: caster,
                target_id: cast.target_id,
                spell_id: cast.spell_id,
                effective_skill: cast.effective_skill,
                duration_seconds: if cast.landed {
                    cast.duration_seconds
                } else {
                    0.0
                },
                landed: cast.landed,
            },
            now,
        );
        if !recorded {
            return false;
        }
        if let Some(transport) = link.transport.as_mut() {
            let Some(own) = registry.capture_own_casts(0, now).pop() else {
                return false;
            };
            return transport.send_cast(own);
        }
        if registry.is_cast_write_due(now) {
            self.publish_now();
        }
        true
    }

    /// The casts others announced (OpenAC `CaptureCasts`): only spells this
    /// client's table names.
    pub(super) fn peers_casts(&mut self, after: i64) -> Vec<PeerCast> {
        let Some(character) = self.character.clone() else {
            return Vec::new();
        };
        let now = self.peers_now();
        let Some(registry) = self.state.plugin_services.peers.registry.as_mut() else {
            return Vec::new();
        };
        let casts = registry.remote_casts(after, &character.world, character.id, now);
        let Some(book) = self.state.world.spellbook() else {
            return Vec::new();
        };
        casts
            .into_iter()
            .filter(|c| book.try_get_metadata(c.spell_id).is_some())
            .collect()
    }

    /// The labels this client answers to (OpenAC `SetTags`): a label too
    /// long to travel refuses the lot.
    pub(super) fn peers_set_tags(&mut self, tags: &[String]) -> bool {
        if tags
            .iter()
            .any(|t| t.trim().encode_utf16().count() > MAXIMUM_TAG_LENGTH)
        {
            return false;
        }
        self.state.plugin_services.peers.tags = normalize_peer_tags(tags);
        self.publish_now();
        true
    }

    /// Broadcasts a line (OpenAC `BroadcastCommand`): kept in this client's
    /// ring for a relay; with the hub, sent at once (this client's record
    /// first), which needs a plugin taking part in something.
    pub(super) fn peers_broadcast(&mut self, broadcast: Broadcast) -> bool {
        let Some(sender) = self.character.as_ref().map(|c| c.id).filter(|id| *id != 0) else {
            return false;
        };
        let now = self.peers_now();
        let tags = broadcast.tags.clone();
        let line = broadcast.line.clone();
        let delay = broadcast.delay_ms;
        let Some(registry) = self.state.plugin_services.peers.registry.as_mut() else {
            return false;
        };
        let recorded = registry.record_command(
            &LocalCommand {
                sender_id: sender,
                tags: broadcast.tags,
                line: broadcast.line,
                delay_ms: broadcast.delay_ms,
            },
            now,
        );
        if !recorded {
            return false;
        }
        if self.state.plugin_services.peers.transport.is_some() {
            let Some(me) = self.peers_capture_self() else {
                return false;
            };
            let link = &mut self.state.plugin_services.peers;
            let (Some(transport), Some(registry)) =
                (link.transport.as_mut(), link.registry.as_mut())
            else {
                return false;
            };
            transport.update_self(me, registry);
            return transport.send_command(&line, normalize_peer_tags(&tags), delay, now, registry);
        }
        if self
            .state
            .plugin_services
            .peers
            .registry
            .as_ref()
            .is_some_and(|r| r.is_command_write_due(now))
        {
            self.publish_now();
        }
        true
    }

    /// The lines others broadcast to this client (OpenAC `CaptureCommands`;
    /// the client runs them itself, this is the plugin's read).
    pub(super) fn peers_commands(&mut self, after: i64) -> Vec<PeerCommand> {
        let Some(character) = self.character.clone() else {
            return Vec::new();
        };
        let now = self.peers_now();
        let link = &mut self.state.plugin_services.peers;
        let tags = link.tags.clone();
        link.registry.as_mut().map_or_else(Vec::new, |r| {
            r.remote_commands(after, &character.world, character.id, &tags, now)
                .into_iter()
                .map(|c| c.command)
                .collect()
        })
    }

    fn publish_now(&mut self) {
        let in_world = self.in_world();
        let character = self.character.clone();
        let now = self.peers_now();
        publish(self.state, character.as_ref(), in_world, now);
    }
}
