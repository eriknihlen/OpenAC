//! The peer hub and this client's line to it. Cases from OpenAC
//! `tests/AcDream.Runtime.Tests/Plugins/PeerHubTests.cs` (named per test),
//! the routing driven directly and the whole path over the real local
//! endpoint.

use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use ac_plugin::abi::peers::{NetworkClient, PeerCast};
use ac_plugin::abi::world::Position;
use ac_plugin_net::local::LocalEndpoint;

use super::peer_hub::{ConnectionId, Hub};
use super::peer_transport::{PeerTransport, StartHub, endpoint_for};
use super::peer_wire::{CASTS, CLIENT_STATE, COMMANDS, PeerOutbox, SEND_COMMANDS, WireMessage};
use super::peers::{InstanceId, PeerRegistry};

const NOW: i64 = 1_790_000_000_000;

fn client(player: u32, name: &str, world: &str, tags: &[&str]) -> NetworkClient {
    NetworkClient {
        client_id: player,
        player_id: player,
        name: name.into(),
        world_name: world.into(),
        position: Position {
            cell_id: 0x7F7F_0001,
            east_west: 1.0,
            north_south: 1.0,
            elevation: 1.0,
            heading_degrees: 0.0,
            is_outdoor: true,
        },
        tags: tags.iter().map(|t| (*t).to_owned()).collect(),
        current_health: 100,
        current_mana: 100,
        current_stamina: 100,
        max_health: 100,
        max_mana: 100,
        max_stamina: 100,
        heading: 0.0,
        is_remote: false,
    }
}

fn instance(n: u8) -> InstanceId {
    InstanceId([n; 16])
}

/// A hub with connections whose queues the test reads.
struct Fixture {
    hub: Hub,
    outboxes: Vec<Arc<PeerOutbox>>,
    closed: Arc<Mutex<Vec<ConnectionId>>>,
}

impl Fixture {
    fn new() -> Self {
        Self {
            hub: Hub::default(),
            outboxes: vec![Arc::new(PeerOutbox::default())],
            closed: Arc::new(Mutex::new(Vec::new())),
        }
    }

    /// A connection registered as `player` in `world` (OpenAC
    /// `HubFixture.Connect`, which waits for `ready`).
    fn connect(
        &mut self,
        player: u32,
        name: &str,
        world: &str,
        capabilities: u32,
        tags: &[&str],
    ) -> ConnectionId {
        let connection = self.outboxes.len() as ConnectionId;
        let outbox = Arc::new(PeerOutbox::default());
        self.outboxes.push(Arc::clone(&outbox));
        let closed = Arc::clone(&self.closed);
        self.hub.open(
            connection,
            outbox,
            Box::new(move || closed.lock().unwrap().push(connection)),
        );
        self.hub
            .handle(
                connection,
                WireMessage {
                    id: instance(u8::try_from(player).unwrap()),
                    client: client(player, name, world, tags),
                    capabilities,
                    ..WireMessage::of("register")
                },
                NOW,
            )
            .unwrap();
        self.read_kind(connection, "ready");
        connection
    }

    /// Everything queued for `connection`.
    fn drain(&self, connection: ConnectionId) -> Vec<WireMessage> {
        let outbox = &self.outboxes[connection as usize];
        std::iter::from_fn(|| outbox.next(Duration::ZERO)).collect()
    }

    fn read_kind(&self, connection: ConnectionId, kind: &str) -> WireMessage {
        self.drain(connection)
            .into_iter()
            .find(|m| m.kind == kind)
            .unwrap_or_else(|| panic!("no {kind} for connection {connection}"))
    }
}

// OpenAC `NewCastListenerDemandArrivesBeforeItsState`.
#[test]
fn a_new_cast_listeners_demand_arrives_before_its_state() {
    let mut f = Fixture::new();
    let source = f.connect(1, "source", "world", CASTS, &[]);
    f.connect(2, "listener", "world", CASTS, &[]);
    let queued = f.drain(source);
    let demand = queued.iter().position(|m| m.kind == "demand").unwrap();
    let state = queued.iter().position(|m| m.kind == "state").unwrap();
    assert!(demand < state);
    assert!(queued[demand].capabilities & CASTS != 0);
    assert_eq!(queued[state].client.name, "listener");
}

// OpenAC `CommandsArePushedOnlyToSubscribedMatchingWorldAndTags`.
#[test]
fn commands_are_pushed_only_to_subscribed_matching_world_and_tags() {
    let mut f = Fixture::new();
    let sender = f.connect(1, "one", "world", SEND_COMMANDS, &["army"]);
    let receiver = f.connect(2, "two", "world", COMMANDS, &["army"]);
    let other_world = f.connect(3, "three", "other", COMMANDS, &["army"]);
    let disabled = f.connect(4, "four", "world", CLIENT_STATE, &["army"]);
    for c in [sender, receiver, other_world, disabled] {
        f.drain(c);
    }
    let id = instance(42);
    f.hub
        .handle(
            sender,
            WireMessage {
                id,
                line: "/example".into(),
                tags: vec!["army".into()],
                delay_ms: 500,
                created_unix_ms: NOW,
                ..WireMessage::of("command")
            },
            NOW,
        )
        .unwrap();
    let delivered = f.read_kind(receiver, "command");
    assert_eq!(delivered.id, id);
    assert_eq!(delivered.delay_ms, 500);
    assert_eq!(delivered.line, "/example");
    assert_eq!(delivered.client.name, "one");
    assert!(f.read_kind(sender, "ack").accepted);
    assert!(f.drain(other_world).iter().all(|m| m.kind != "command"));
    assert!(f.drain(disabled).iter().all(|m| m.kind != "command"));
    // From another world nobody receives it, and it is still acknowledged.
    f.hub
        .handle(
            other_world,
            WireMessage {
                id: instance(43),
                line: "/other".into(),
                created_unix_ms: NOW,
                ..WireMessage::of("command")
            },
            NOW,
        )
        .unwrap();
    f.read_kind(other_world, "ack");
    // Sending does not subscribe the sender to receive commands.
    f.hub
        .handle(
            receiver,
            WireMessage {
                id: instance(44),
                line: "/reply".into(),
                created_unix_ms: NOW,
                ..WireMessage::of("command")
            },
            NOW,
        )
        .unwrap();
    assert!(f.read_kind(receiver, "ack").accepted);
    assert!(f.drain(sender).iter().all(|m| m.kind != "command"));
}

// OpenAC `PeerHubServer` command routing: recipients by client id, each
// waiting one more stagger than the one before.
#[test]
fn each_recipient_waits_one_more_stagger_by_client_id() {
    let mut f = Fixture::new();
    let sender = f.connect(1, "one", "world", SEND_COMMANDS, &[]);
    let late = f.connect(9, "nine", "world", COMMANDS, &[]);
    let early = f.connect(5, "five", "world", COMMANDS, &[]);
    f.hub
        .handle(
            sender,
            WireMessage {
                id: instance(50),
                line: "/go".into(),
                delay_ms: 100,
                created_unix_ms: NOW,
                ..WireMessage::of("command")
            },
            NOW,
        )
        .unwrap();
    assert_eq!(f.read_kind(early, "command").delay_ms, 100);
    assert_eq!(f.read_kind(late, "command").delay_ms, 200);
    // A line older than 15 s, or a control character, is refused.
    let stale = f.hub.handle(
        sender,
        WireMessage {
            id: instance(51),
            line: "/go".into(),
            created_unix_ms: NOW - 15_001,
            ..WireMessage::of("command")
        },
        NOW,
    );
    assert_eq!(stale, Err("Invalid peer command.".to_owned()));
}

// OpenAC `DuplicateIdentityDisconnectDoesNotBlockOtherClientsOrShutdown`.
#[test]
fn a_duplicate_identity_is_refused_and_the_others_go_on() {
    let mut f = Fixture::new();
    let first = f.connect(1, "one", "world", CLIENT_STATE, &[]);
    let duplicate = f.outboxes.len() as ConnectionId;
    f.outboxes.push(Arc::new(PeerOutbox::default()));
    f.hub.open(
        duplicate,
        Arc::clone(&f.outboxes[duplicate as usize]),
        Box::new(|| {}),
    );
    let refused = f.hub.handle(
        duplicate,
        WireMessage {
            id: instance(1),
            client: client(2, "duplicate", "world", &[]),
            capabilities: CLIENT_STATE,
            ..WireMessage::of("register")
        },
        NOW,
    );
    assert!(refused.unwrap_err().contains("duplicate identity"));
    f.hub.close(duplicate);
    f.connect(3, "three", "world", CLIENT_STATE, &[]);
    assert_eq!(f.read_kind(first, "state").client.name, "three");
}

// OpenAC `StateDemandRequiresAnotherParticipantAndDisconnectRemovesPeer`.
#[test]
fn state_demand_needs_another_participant_and_a_disconnect_removes_the_peer() {
    let mut f = Fixture::new();
    let first = f.connect(1, "one", "world", CLIENT_STATE, &[]);
    assert!(f.drain(first).is_empty(), "nobody else yet: no demand");
    let second = f.connect(2, "two", "world", CLIENT_STATE, &[]);
    let queued = f.drain(first);
    let state = queued.iter().find(|m| m.kind == "state").unwrap();
    assert_eq!(state.client.name, "two");
    let demand = queued.iter().find(|m| m.kind == "demand").unwrap();
    assert!(demand.capabilities & CLIENT_STATE != 0);
    f.hub.close(second);
    let queued = f.drain(first);
    assert_eq!(
        queued.iter().find(|m| m.kind == "remove").unwrap().id,
        instance(2)
    );
    assert_eq!(
        queued
            .iter()
            .find(|m| m.kind == "demand")
            .unwrap()
            .capabilities,
        0
    );
}

// OpenAC `PeerHubServer.Send`: a queue that overflows closes that
// connection, so the gap shows.
#[test]
fn an_overflowing_queue_closes_its_connection() {
    let mut f = Fixture::new();
    let sender = f.connect(1, "one", "world", CASTS, &[]);
    let listener = f.connect(2, "two", "world", CASTS, &[]);
    let cast = PeerCast {
        sequence: 1,
        caster_id: 1,
        target_id: 7,
        spell_id: 100,
        effective_skill: 200,
        seconds_remaining: 20.0,
        landed: true,
        ..PeerCast::default()
    };
    for _ in 0..300 {
        f.hub
            .handle(
                sender,
                WireMessage {
                    cast: cast.clone(),
                    ..WireMessage::of("cast")
                },
                NOW,
            )
            .unwrap();
    }
    let closed = f.closed.lock().unwrap().clone();
    assert!(
        !closed.is_empty() && closed.iter().all(|c| *c == listener),
        "{closed:?}"
    );
}

struct Folder(PathBuf);

impl Folder {
    fn new(tag: &str) -> Self {
        let path = std::env::temp_dir().join(format!("openac-hub-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&path);
        Self(path)
    }
}

impl Drop for Folder {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn no_start() -> StartHub {
    Arc::new(|| Err(std::io::Error::other("Test hub is already running.")))
}

/// A hub served on its own thread for `folder`'s endpoint.
fn serve(endpoint: &LocalEndpoint) {
    let endpoint = endpoint.clone();
    std::thread::spawn(move || {
        let _ = super::peer_hub::serve(&endpoint, |_| {});
    });
}

fn until(mut done: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(10);
    while !done() {
        assert!(Instant::now() < deadline, "timed out");
        std::thread::sleep(Duration::from_millis(10));
    }
}

fn now_ms() -> i64 {
    i64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis(),
    )
    .unwrap()
}

// OpenAC `CastsArriveThroughMemoryWithoutAnyPeerFiles`, both clients being
// this client's transport over a real hub: one shares a cast, the other
// reads it from memory with what is left of it, and no note is written.
#[test]
fn casts_arrive_through_memory_without_any_peer_files() {
    let folder = Folder::new("casts");
    let endpoint = endpoint_for(&folder.0);
    serve(&endpoint);
    let mut registries = [
        PeerRegistry::in_memory(&folder.0, instance(1)),
        PeerRegistry::in_memory(&folder.0, instance(2)),
    ];
    let mut transports = [
        PeerTransport::new(endpoint.clone(), instance(1), no_start()),
        PeerTransport::new(endpoint.clone(), instance(2), no_start()),
    ];
    let names = ["one", "two"];
    let mut leases = Vec::new();
    for i in 0..2 {
        let player = u32::try_from(i + 1).unwrap();
        transports[i].update_self(client(player, names[i], "world", &[]), &mut registries[i]);
        leases.push(transports[i].subscribe(CASTS, &mut registries[i]).unwrap());
    }
    // Registered, each seeing the other, and the listener's demand for
    // casts reached the caster.
    until(|| {
        for i in 0..2 {
            transports[i].pump(&mut registries[i], now_ms());
        }
        transports.iter().all(PeerTransport::is_connected)
            && registries[1].remote_clients(2, now_ms()).len() == 1
            && transports[0].demand() & CASTS != 0
    });
    let cast = PeerCast {
        sequence: 1,
        client_id: registries[0].client_id(),
        caster_id: 1,
        target_id: 9,
        spell_id: 100,
        effective_skill: 200,
        seconds_remaining: 20.0,
        landed: true,
        is_remote: false,
    };
    assert!(transports[0].send_cast(cast));
    until(|| {
        transports[1].pump(&mut registries[1], now_ms());
        !registries[1]
            .remote_casts(0, "world", 2, now_ms())
            .is_empty()
    });
    let casts = registries[1].remote_casts(0, "world", 2, now_ms());
    assert_eq!(casts.len(), 1);
    assert_eq!(casts[0].spell_id, 100);
    assert!((15.0..=20.0).contains(&casts[0].seconds_remaining));
    let notes = std::fs::read_dir(&folder.0)
        .unwrap()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().starts_with("peer-"))
        .count();
    assert_eq!(notes, 0);
    transports[1].release(leases[1], &mut registries[1]);
    assert!(!transports[1].is_active());
    assert!(registries[1].remote_clients(2, now_ms()).is_empty());
    for i in 0..2 {
        transports[i].dispose(&mut registries[i]);
    }
}

// OpenAC `ZeroSubscriptionsNeverStartTransportAndLeasesCombine`.
#[test]
fn no_subscription_never_starts_the_transport_and_leases_combine() {
    let folder = Folder::new("zero");
    let starts = Arc::new(AtomicUsize::new(0));
    let counted = Arc::clone(&starts);
    let start: StartHub = Arc::new(move || {
        counted.fetch_add(1, Ordering::SeqCst);
        Ok(())
    });
    let mut registry = PeerRegistry::in_memory(&folder.0, instance(1));
    let mut transport = PeerTransport::new(endpoint_for(&folder.0), instance(1), start);
    transport.update_self(client(1, "one", "world", &[]), &mut registry);
    assert!(!transport.is_active());
    std::thread::sleep(Duration::from_millis(100));
    assert_eq!(starts.load(Ordering::SeqCst), 0);
    assert!(!folder.0.exists());
    let casts = transport.subscribe(CASTS, &mut registry).unwrap();
    let state = transport.subscribe(CLIENT_STATE, &mut registry).unwrap();
    transport.release(casts, &mut registry);
    assert!(transport.has(CLIENT_STATE));
    assert!(!transport.has(CASTS));
    transport.release(state, &mut registry);
    assert!(!transport.is_active());
    transport.dispose(&mut registry);
}

// OpenAC `LocalPeerTransport.RunAsync`: no hub answers, so the first
// failure starts one, and the client connects to it once it runs.
#[test]
fn a_missing_hub_is_started_and_then_used() {
    let folder = Folder::new("start");
    let endpoint = endpoint_for(&folder.0);
    let starts = Arc::new(AtomicUsize::new(0));
    let counted = Arc::clone(&starts);
    let hub_endpoint = endpoint.clone();
    let start: StartHub = Arc::new(move || {
        counted.fetch_add(1, Ordering::SeqCst);
        serve(&hub_endpoint);
        Ok(())
    });
    let mut registry = PeerRegistry::in_memory(&folder.0, instance(1));
    let mut transport = PeerTransport::new(endpoint, instance(1), start);
    transport.update_self(client(1, "one", "world", &[]), &mut registry);
    transport.subscribe(CLIENT_STATE, &mut registry).unwrap();
    until(|| {
        transport.pump(&mut registry, now_ms());
        transport.is_connected()
    });
    assert_eq!(starts.load(Ordering::SeqCst), 1);
    transport.dispose(&mut registry);
}

// Two clients through a real hub: a broadcast reaches the client receiving
// lines and wearing the label, with its stagger, and is acknowledged.
#[test]
fn a_broadcast_reaches_the_labelled_receiver_through_the_hub() {
    let folder = Folder::new("broadcast");
    let endpoint = endpoint_for(&folder.0);
    serve(&endpoint);
    let mut sender_registry = PeerRegistry::in_memory(&folder.0, instance(1));
    let mut receiver_registry = PeerRegistry::in_memory(&folder.0, instance(2));
    let mut sender = PeerTransport::new(endpoint.clone(), instance(1), no_start());
    let mut receiver = PeerTransport::new(endpoint, instance(2), no_start());
    receiver.update_self(
        client(2, "two", "world", &["healer"]),
        &mut receiver_registry,
    );
    receiver
        .subscribe(COMMANDS, &mut receiver_registry)
        .unwrap();
    sender.update_self(client(1, "one", "world", &[]), &mut sender_registry);
    sender
        .subscribe(CLIENT_STATE, &mut sender_registry)
        .unwrap();
    until(|| {
        sender.pump(&mut sender_registry, now_ms());
        receiver.pump(&mut receiver_registry, now_ms());
        sender.is_connected() && receiver.is_connected()
    });
    assert!(sender.send_command(
        "/heal me",
        vec!["healer".into()],
        250,
        now_ms(),
        &mut sender_registry
    ));
    let mut delivered = Vec::new();
    until(|| {
        sender.pump(&mut sender_registry, now_ms());
        delivered.extend(receiver.pump(&mut receiver_registry, now_ms()));
        !delivered.is_empty()
    });
    assert_eq!(delivered[0].line, "/heal me");
    assert_eq!(delivered[0].delay_ms, 250);
    assert_eq!(delivered[0].sender.name, "one");
    // The acknowledgement gives the send lease back.
    until(|| {
        sender.pump(&mut sender_registry, now_ms());
        !sender.has(SEND_COMMANDS)
    });
    sender.dispose(&mut sender_registry);
    receiver.dispose(&mut receiver_registry);
}

// OpenAC (origin/main) `ScopedPeerAutomation`: a lease belongs to the
// plugin that took it; the plugin gives it back when it stops sharing
// (MosswartMassacre `VitalSharing.Stop`), and whatever it still holds is
// given back when it is unloaded. Through the binding, over the session's
// hub connection.
#[test]
fn a_plugin_gives_back_its_peer_lease_and_the_rest_go_when_it_unloads() {
    use ac_plugin::SessionApi;
    use ac_plugin::abi::calls;

    use super::tests::{call, in_world, with_binding};
    use crate::session::Outbox;

    let folder = Folder::new("leases");
    let mut state = in_world();
    let mut outbox = Outbox::new();
    // Peer communication off: no lease.
    with_binding(&mut state, &mut outbox, |b| {
        assert_eq!(call::<calls::PeersSubscribe>(b, &CASTS), None);
    });
    state.plugin_services.peers.configure_with_hub(
        &folder.0,
        instance(1),
        &[],
        endpoint_for(&folder.0),
        no_start(),
    );
    let active = |state: &crate::session::SessionState| {
        state
            .plugin_services
            .peers
            .transport()
            .is_some_and(PeerTransport::is_active)
    };
    let sharing = with_binding(&mut state, &mut outbox, |b| {
        call::<calls::PeersSubscribe>(b, &(CLIENT_STATE | CASTS)).expect("a lease")
    });
    assert!(active(&state));
    with_binding(&mut state, &mut outbox, |b| {
        assert!(call::<calls::PeersUnsubscribe>(b, &sharing));
        assert!(!call::<calls::PeersUnsubscribe>(b, &sharing));
    });
    assert!(!active(&state));
    with_binding(&mut state, &mut outbox, |b| {
        call::<calls::PeersSubscribe>(b, &CASTS).expect("a lease");
    });
    assert!(active(&state));
    with_binding(&mut state, &mut outbox, |b| b.released("test.plugin"));
    assert!(!active(&state));
}
