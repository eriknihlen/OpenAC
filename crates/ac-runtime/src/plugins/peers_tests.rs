//! The peer registry. Cases and golden values from OpenAC
//! `tests/AcDream.Runtime.Tests/Plugins/LocalPluginPeerRegistryTests.cs`,
//! named per test.

use std::path::{Path, PathBuf};

use ac_plugin::abi::peers::NetworkClient;
use ac_plugin::abi::world::Position;

use super::peers::{
    CAST_RING_CAPACITY, CAST_WRITE_DEBOUNCE_MS, InstanceId, LocalCast, LocalCommand, PeerRegistry,
    STALE_AFTER_MS,
};

/// 2026-09-21 12:00 UTC.
const NOON: i64 = 1_790_000_000_000;

struct Root(PathBuf);

impl Root {
    fn new(tag: &str) -> Self {
        let root =
            std::env::temp_dir().join(format!("openac-peers-a1c8-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        Self(root)
    }
}

impl Drop for Root {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Registry `which`, running as GUID `{which:08}-0000-...` (client id
/// `which`).
fn registry(root: &Path, which: u32) -> PeerRegistry {
    let mut bytes = [0u8; 16];
    bytes[..4].copy_from_slice(&which.to_be_bytes());
    PeerRegistry::new(root, InstanceId(bytes))
}

fn client(client_id: u32, player_id: u32, name: &str, tags: &[&str]) -> NetworkClient {
    NetworkClient {
        client_id,
        player_id,
        name: name.into(),
        world_name: "Coldeve".into(),
        position: Position {
            cell_id: 0x7F7F_0001,
            east_west: 33.5,
            north_south: -72.8,
            elevation: 1.0,
            heading_degrees: 90.0,
            is_outdoor: true,
        },
        tags: tags.iter().map(|t| (*t).to_owned()).collect(),
        current_health: 90,
        current_mana: 70,
        current_stamina: 80,
        max_health: 100,
        max_mana: 100,
        max_stamina: 100,
        heading: 90.0,
        is_remote: false,
    }
}

fn landed(caster: u32, target: u32, spell: u32) -> LocalCast {
    LocalCast {
        caster_id: caster,
        target_id: target,
        spell_id: spell,
        effective_skill: 357,
        duration_seconds: 60.0,
        landed: true,
    }
}

fn line(sender: u32, tags: &[&str], text: &str, delay_ms: i32) -> LocalCommand {
    LocalCommand {
        sender_id: sender,
        tags: tags.iter().map(|t| (*t).to_owned()).collect(),
        line: text.into(),
        delay_ms,
    }
}

// OpenAC `PublishesRemoteClientsIgnoresSelfAndExpiresStaleHeartbeat`.
#[test]
fn publishes_remote_clients_ignores_self_and_expires_stale_heartbeat() {
    let root = Root::new("clients");
    let mut first = registry(&root.0, 0x1111_1111);
    let mut second = registry(&root.0, 0x2222_2222);
    first.publish(&client(first.client_id(), 10, "Alpha", &["one"]), NOON);
    second.publish(&client(second.client_id(), 20, "Beta", &["two"]), NOON);
    let remote = first.remote_clients(0, NOON);
    assert_eq!(remote.len(), 1);
    assert_eq!(remote[0].client_id, second.client_id());
    assert_eq!(remote[0].name, "Beta");
    assert_eq!(remote[0].tags, ["two"]);
    assert!((remote[0].position.east_west - 33.5).abs() < 1e-12);
    assert!(
        first
            .remote_clients(0, NOON + STALE_AFTER_MS + 1)
            .is_empty()
    );
}

// OpenAC `ACastCrossesToTheOtherClientOnceAndNeverComesBackAsItsOwn`.
#[test]
fn a_cast_crosses_to_the_other_client_once_and_never_comes_back_as_its_own() {
    let root = Root::new("cast");
    let mut caster = registry(&root.0, 1);
    let mut reader = registry(&root.0, 2);
    caster.publish(&client(caster.client_id(), 10, "Alpha", &["one"]), NOON);
    reader.publish(&client(reader.client_id(), 20, "Beta", &["two"]), NOON);
    assert!(caster.record_cast(&landed(10, 0x5000_0012, 42), NOON));
    // A note that claims the reader cast something.
    assert!(caster.record_cast(&landed(20, 0x5000_0012, 43), NOON));
    let later = NOON + CAST_WRITE_DEBOUNCE_MS;
    caster.publish(&client(caster.client_id(), 10, "Alpha", &["one"]), later);
    let casts = reader.remote_casts(0, "Coldeve", 20, later);
    assert_eq!(casts.len(), 1);
    let only = &casts[0];
    assert_eq!(only.client_id, caster.client_id());
    assert_eq!(only.caster_id, 10);
    assert_eq!(only.target_id, 0x5000_0012);
    assert_eq!(only.spell_id, 42);
    assert_eq!(only.effective_skill, 357);
    assert!(only.landed);
    assert!((only.seconds_remaining - (60.0 - 0.25)).abs() < 1e-3);
    assert!(
        reader
            .remote_casts(only.sequence, "Coldeve", 20, later)
            .is_empty()
    );
    assert_eq!(reader.remote_casts(0, "Coldeve", 20, later).len(), 1);
}

// OpenAC `ACastAgesOutOfTheWindowEvenWhileItsClientKeepsHeartbeating` and
// `ACastFromAClientInAnotherWorldIsSkipped`.
#[test]
fn a_cast_ages_out_and_one_from_another_world_is_skipped() {
    let root = Root::new("age");
    let mut caster = registry(&root.0, 1);
    let mut reader = registry(&root.0, 2);
    caster.record_cast(&landed(10, 0x5000_0012, 42), NOON);
    caster.publish(&client(caster.client_id(), 10, "Alpha", &[]), NOON);
    assert!(reader.remote_casts(0, "Frostfell", 20, NOON).is_empty());
    assert_eq!(reader.remote_clients(0, NOON).len(), 1);
    assert_eq!(reader.remote_casts(0, "Coldeve", 20, NOON).len(), 1);
    caster.publish(&client(caster.client_id(), 10, "Alpha", &[]), NOON + 10_000);
    assert_eq!(
        reader.remote_casts(0, "Coldeve", 20, NOON + 10_000).len(),
        1
    );
    caster.publish(&client(caster.client_id(), 10, "Alpha", &[]), NOON + 20_000);
    assert!(
        reader
            .remote_casts(0, "Coldeve", 20, NOON + 20_000)
            .is_empty()
    );
}

// OpenAC `TheRingKeepsTheMostRecentCastsAndNoMore`.
#[test]
fn the_ring_keeps_the_most_recent_casts_and_no_more() {
    let root = Root::new("ring");
    let mut caster = registry(&root.0, 1);
    let mut reader = registry(&root.0, 2);
    for index in 1..=40u32 {
        caster.record_cast(&landed(10, 0x5000_0000 + index, 40 + index), NOON);
    }
    caster.publish(&client(caster.client_id(), 10, "Alpha", &[]), NOON);
    let read = reader.remote_casts(0, "Coldeve", 20, NOON);
    assert_eq!(read.len(), CAST_RING_CAPACITY);
    assert_eq!(read[0].target_id, 0x5000_0009);
    assert_eq!(read[read.len() - 1].target_id, 0x5000_0028);
}

// OpenAC `ABurstOfCastsAsksForOneWriteRatherThanOneEach`.
#[test]
fn a_burst_of_casts_asks_for_one_write_rather_than_one_each() {
    let root = Root::new("burst");
    let mut caster = registry(&root.0, 1);
    let mut reader = registry(&root.0, 2);
    caster.publish(&client(caster.client_id(), 10, "Alpha", &[]), NOON);
    assert!(!caster.is_cast_write_due(NOON));
    caster.record_cast(&landed(10, 0x5000_0012, 42), NOON);
    assert!(!caster.is_cast_write_due(NOON));
    caster.record_cast(&landed(10, 0x5000_0013, 43), NOON + 50);
    assert!(!caster.is_cast_write_due(NOON + 50));
    let later = NOON + 50 + CAST_WRITE_DEBOUNCE_MS;
    assert!(caster.is_cast_write_due(later));
    caster.publish(&client(caster.client_id(), 10, "Alpha", &[]), later);
    assert!(!caster.is_cast_write_due(later));
    assert_eq!(reader.remote_casts(0, "Coldeve", 20, later).len(), 2);
}

// OpenAC `ANoteIsBelievedOnlyWhereItsCastMakesSense`: the one rule on the
// way in.
#[test]
fn a_cast_is_recorded_only_where_it_makes_sense() {
    let root = Root::new("sense");
    let mut caster = registry(&root.0, 1);
    for (target, spell, duration, landed, recorded) in [
        (0, 42, 60.0, true, false),
        (0x5000_0012, 0, 60.0, true, false),
        (0x5000_0012, 42, f64::NAN, true, false),
        (0x5000_0012, 42, f64::INFINITY, true, false),
        (0x5000_0012, 42, 0.0, true, false),
        (0x5000_0012, 42, 604_800.0, true, false),
        (0x5000_0012, 42, 0.0, false, true),
        (0x5000_0012, 42, 60.0, true, true),
    ] {
        let cast = LocalCast {
            caster_id: 10,
            target_id: target,
            spell_id: spell,
            effective_skill: 357,
            duration_seconds: duration,
            landed,
        };
        assert_eq!(caster.record_cast(&cast, NOON), recorded, "{cast:?}");
    }
}

// OpenAC `APositionThatIsNotANumberIsRefusedRatherThanThrownAtTheCaller`
// and `AWriteThatCouldNotBeDoneWaitsTheHeartbeatBeforeTheNextTry`.
#[test]
fn a_position_that_is_not_a_number_is_refused_and_holds_writes_back() {
    let root = Root::new("nan");
    let mut caster = registry(&root.0, 1);
    let mut bad = client(caster.client_id(), 10, "Alpha", &[]);
    bad.position.east_west = f64::NAN;
    caster.record_cast(&landed(10, 0x5000_0012, 42), NOON);
    assert!(!caster.publish(&bad, NOON));
    assert!(!caster.is_cast_write_due(NOON + 1_000));
    assert!(caster.is_cast_write_due(NOON + 5_000));
}

// OpenAC `BytesThatAreNotANoteCostTheReaderNothing` and
// `AFileClaimingAPeersIdentityCannotSilenceThatPeer` (a note claiming this
// client's own identity is not read).
#[test]
fn bytes_that_are_not_a_note_cost_the_reader_nothing() {
    let root = Root::new("bytes");
    let mut reader = registry(&root.0, 2);
    std::fs::create_dir_all(&root.0).unwrap();
    for (name, text) in [
        ("peer-aaaa.json", "not json"),
        ("peer-bbbb.json", "[]"),
        (
            "peer-cccc.json",
            "{\"InstanceId\":\"00000003-0000-0000-0000-000000000000\",\"Name\":null}",
        ),
        (
            "peer-dddd.json",
            "{\"InstanceId\":\"00000002-0000-0000-0000-000000000000\",\"ClientId\":2,\"PlayerId\":20,\"Name\":\"Me\",\"WorldName\":\"Coldeve\",\"UpdatedUnixMs\":1790000000000}",
        ),
    ] {
        std::fs::write(root.0.join(name), text).unwrap();
    }
    assert!(reader.remote_clients(0, NOON).is_empty());
    // A hand-written note in the documented shape, in any case, is read.
    std::fs::write(
        root.0.join("peer-eeee.json"),
        "{\"instanceid\":\"00000005-0000-0000-0000-000000000000\",\"clientid\":5,\"playerid\":50,\"name\":\"Hand\",\"worldname\":\"Coldeve\",\"updatedunixms\":1790000000000,\"Unknown\":true}",
    )
    .unwrap();
    let clients = reader.remote_clients(0, NOON);
    assert_eq!(clients.len(), 1);
    assert_eq!(clients[0].name, "Hand");
}

// OpenAC `ANoteWithOneImpossibleCastLosesItsCastRing` and
// `ABadRowInOneRingLeavesTheOtherRingAndTheClientAlone`.
#[test]
fn a_bad_row_loses_only_its_own_ring() {
    let root = Root::new("row");
    let mut reader = registry(&root.0, 2);
    std::fs::create_dir_all(&root.0).unwrap();
    std::fs::write(
        root.0.join("peer-0000000300000000000000000000000.json"),
        "{\"InstanceId\":\"00000003-0000-0000-0000-000000000000\",\"ClientId\":3,\"PlayerId\":30,\"Name\":\"Gamma\",\"WorldName\":\"Coldeve\",\"UpdatedUnixMs\":1790000000000,\
         \"Casts\":[{\"Sequence\":1,\"AtUnixMs\":1790000000000,\"CasterObjectId\":30,\"TargetObjectId\":5,\"SpellId\":42,\"EffectiveSkill\":1,\"DurationSeconds\":60,\"Landed\":true},null],\
         \"Commands\":[{\"Sequence\":1,\"AtUnixMs\":1790000000000,\"SenderObjectId\":30,\"Tags\":[],\"Line\":\"/hello\",\"DelayMilliseconds\":0}]}",
    )
    .unwrap();
    assert_eq!(reader.remote_clients(0, NOON).len(), 1);
    assert!(reader.remote_casts(0, "Coldeve", 20, NOON).is_empty());
    let lines = reader.remote_commands(0, "Coldeve", 20, &[], NOON);
    assert_eq!(lines.len(), 1);
    assert_eq!(lines[0].command.line, "/hello");
}

// OpenAC `ACommandLineCrossesToTheOtherClientOnceAndNeverComesBackAsItsOwn`.
#[test]
fn a_command_line_crosses_once_and_never_comes_back_as_its_own() {
    let root = Root::new("line");
    let mut sender = registry(&root.0, 1);
    let mut reader = registry(&root.0, 2);
    sender.publish(&client(sender.client_id(), 10, "Alpha", &[]), NOON);
    reader.publish(&client(reader.client_id(), 20, "Beta", &[]), NOON);
    assert!(sender.record_command(&line(10, &[], "/example go", 0), NOON));
    assert!(sender.record_command(&line(20, &[], "/example stop", 0), NOON));
    sender.publish(&client(sender.client_id(), 10, "Alpha", &[]), NOON);
    let lines = reader.remote_commands(0, "Coldeve", 20, &[], NOON);
    assert_eq!(lines.len(), 1);
    let only = &lines[0];
    assert_eq!(only.command.client_id, sender.client_id());
    assert_eq!(only.command.sender_id, 10);
    assert_eq!(only.command.line, "/example go");
    assert!(only.command.tags.is_empty());
    assert_eq!(only.stagger_ms, 0);
    assert_eq!(only.command.sent_unix_ms, NOON);
    assert!(
        reader
            .remote_commands(only.command.sequence, "Coldeve", 20, &[], NOON)
            .is_empty()
    );
    assert_eq!(reader.remote_commands(0, "Coldeve", 20, &[], NOON).len(), 1);
}

// OpenAC `ALineOnlyReachesAClientWearingOneOfTheLabelsItIsAimedAt`.
#[test]
fn a_line_only_reaches_a_client_wearing_one_of_its_labels() {
    for (aimed, wearing, reaches) in [
        (&[][..], &["healer"][..], true),
        (&["healer"][..], &["healer"][..], true),
        (&["HEALER"][..], &["healer"][..], true),
        (&["healer", "tank"][..], &["tank"][..], true),
        (&["tank"][..], &["healer"][..], false),
        (&["tank"][..], &[][..], false),
    ] {
        let root = Root::new("labels");
        let mut sender = registry(&root.0, 1);
        let mut reader = registry(&root.0, 2);
        reader.publish(&client(reader.client_id(), 20, "Beta", wearing), NOON);
        assert!(sender.record_command(&line(10, aimed, "/example go", 0), NOON));
        sender.publish(&client(sender.client_id(), 10, "Alpha", &[]), NOON);
        let wearing: Vec<String> = wearing.iter().map(|t| (*t).to_owned()).collect();
        assert_eq!(
            reader
                .remote_commands(0, "Coldeve", 20, &wearing, NOON)
                .len(),
            usize::from(reaches),
            "{aimed:?} {wearing:?}"
        );
    }
}

// OpenAC `TheRecipientsStaggerThemselvesByClientIdWithTheSenderFirst`,
// `AClientTheLineIsNotAimedAtTakesNoPlaceInTheOrder` and
// `AClientThatStoppedSayingSoTakesNoPlaceInTheOrder`.
#[test]
fn the_recipients_stagger_by_client_id_behind_the_sender() {
    let root = Root::new("stagger");
    let mut sender = registry(&root.0, 1);
    let mut first = registry(&root.0, 2);
    let mut second = registry(&root.0, 3);
    sender.publish(&client(1, 10, "Alpha", &[]), NOON);
    first.publish(&client(2, 20, "Beta", &[]), NOON);
    second.publish(&client(3, 30, "Gamma", &[]), NOON);
    assert!(sender.record_command(&line(10, &[], "/example go", 100), NOON));
    sender.publish(&client(1, 10, "Alpha", &[]), NOON);
    assert_eq!(
        first.remote_commands(0, "Coldeve", 20, &[], NOON)[0].stagger_ms,
        100
    );
    assert_eq!(
        second.remote_commands(0, "Coldeve", 30, &[], NOON)[0].stagger_ms,
        200
    );

    let root = Root::new("bystander");
    let mut sender = registry(&root.0, 1);
    let mut bystander = registry(&root.0, 2);
    let mut reader = registry(&root.0, 3);
    sender.publish(&client(1, 10, "Alpha", &[]), NOON);
    bystander.publish(&client(2, 20, "Beta", &["tank"]), NOON);
    reader.publish(&client(3, 30, "Gamma", &["healer"]), NOON);
    assert!(sender.record_command(&line(10, &["healer"], "/example go", 100), NOON));
    sender.publish(&client(1, 10, "Alpha", &[]), NOON);
    let healer = ["healer".to_owned()];
    assert_eq!(
        reader.remote_commands(0, "Coldeve", 30, &healer, NOON)[0].stagger_ms,
        100
    );

    let root = Root::new("crashed");
    let mut sender = registry(&root.0, 1);
    let mut live = registry(&root.0, 2);
    let mut crashed = registry(&root.0, 3);
    let mut reader = registry(&root.0, 4);
    crashed.publish(&client(3, 30, "Gamma", &[]), NOON);
    let later = NOON + 30_000;
    sender.publish(&client(1, 10, "Alpha", &[]), later);
    live.publish(&client(2, 20, "Beta", &[]), later);
    reader.publish(&client(4, 40, "Delta", &[]), later);
    assert!(sender.record_command(&line(10, &[], "/example go", 100), later));
    sender.publish(&client(1, 10, "Alpha", &[]), later);
    assert_eq!(
        reader.remote_commands(0, "Coldeve", 40, &[], later)[0].stagger_ms,
        200
    );
}

// OpenAC `ACommandLineThatMakesNoSenseNeverEntersTheRing` and
// `ALineOrALabelBeyondTheCapsIsRefused`.
#[test]
fn a_line_that_makes_no_sense_never_enters_the_ring() {
    let root = Root::new("caps");
    let mut sender = registry(&root.0, 1);
    let long_tag = "x".repeat(65);
    let many: Vec<String> = (0..17).map(|i| format!("t{i}")).collect();
    for command in [
        line(0, &[], "/go", 0),
        line(10, &[], "   ", 0),
        line(10, &[], "/go\u{7}", 0),
        line(10, &[], "/go", -1),
        line(10, &[], "/go", 60_001),
        line(10, &[long_tag.as_str()], "/go", 0),
        LocalCommand {
            tags: many,
            ..line(10, &[], "/go", 0)
        },
        line(10, &[], &"y".repeat(513), 0),
    ] {
        assert!(!sender.record_command(&command, NOON), "{command:?}");
    }
    assert!(sender.record_command(&line(10, &[], &"y".repeat(512), 60_000), NOON));
}

// OpenAC `RuntimeAutomationSurface` peers: a plugin's broadcast reaches
// another client's registry; a line broadcast to this client runs through
// its tick once its stagger is up (OpenAC `PumpPeerCommands`); tags too
// long to travel are refused.
#[test]
fn the_session_broadcasts_and_runs_lines_once_their_stagger_is_up() {
    use ac_plugin::abi::calls;
    use ac_plugin::abi::peers::Broadcast;

    use super::peer_link;
    use super::tests::{CLOCKS, PLAYER, call, in_world, with_binding};
    use crate::session::Outbox;

    let root = Root::new("link");
    let mut state = in_world();
    let mut me = [0u8; 16];
    me[3] = 5;
    state
        .plugin_services
        .peers
        .configure_as(&root.0, InstanceId(me), &["healer".to_owned()]);
    let mut other = registry(&root.0, 1);
    other.publish(&client(1, 10, "Alpha", &[]), CLOCKS.wall_unix_ms);
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(!call::<calls::PeersSetTags>(b, &vec!["x".repeat(65)]));
        assert!(call::<calls::PeersSetTags>(
            b,
            &vec![" healer ".into(), "HEALER".into()]
        ));
        let go = Broadcast {
            line: "/example go".into(),
            tags: Vec::new(),
            delay_ms: 0,
        };
        assert!(call::<calls::PeersBroadcast>(b, &go));
        assert_eq!(call::<calls::PeersClients>(b, &()).len(), 1);
    });
    // The fixture character has no body to stand anywhere, so its note
    // is withheld (OpenAC `PublishPeerSnapshot` withdraws without a
    // navigation snapshot): the line waits in the ring.
    assert!(other.remote_clients(0, CLOCKS.wall_unix_ms).is_empty());
    assert!(
        other
            .remote_commands(0, "Local", 10, &[], CLOCKS.wall_unix_ms)
            .is_empty()
    );

    // The other client asks the healers to run a line, 100 ms apart.
    let now = CLOCKS.wall_unix_ms;
    other.record_command(&line(10, &["healer"], "/heal me", 100), now);
    let mut alpha = client(1, 10, "Alpha", &[]);
    alpha.world_name = "Local".into();
    other.publish(&alpha, now);
    let character = ac_plugin::abi::CharacterInfo {
        id: PLAYER,
        name: "+Horan".into(),
        world: "Local".into(),
        ..ac_plugin::abi::CharacterInfo::default()
    };
    assert!(peer_link::tick(&mut state, Some(&character), true, 0.3, now).is_empty());
    assert!(peer_link::tick(&mut state, Some(&character), true, 0.015, now + 50).is_empty());
    assert_eq!(
        peer_link::tick(&mut state, Some(&character), true, 0.015, now + 100),
        ["/heal me"]
    );
    assert!(peer_link::tick(&mut state, Some(&character), true, 0.3, now + 400).is_empty());
}

// OpenAC (origin/main) `RuntimeAutomationSurface`'s relay members:
// `IsConnected` and `Subscribe` without a hub, `CaptureOwnCasts` after an
// announcement, `ImportRemoteClient` / `ImportRemoteCast` (classified in
// this client's spell table) / `ImportRemoteCommand` (run by the client as
// a neighbour's line), and `ForgetOwnAnnouncements` when the character
// leaves the world.
#[test]
fn a_relay_reads_this_clients_announcements_and_imports_peers_from_elsewhere() {
    use std::sync::Arc;

    use ac_core::spells::{SpellMetadata, SpellTable};
    use ac_plugin::abi::calls;
    use ac_plugin::abi::peers::{CastAnnouncement, RemoteBroadcast, RemoteCast};

    use super::peer_link;
    use super::tests::{CLOCKS, PLAYER, call, in_world_with, with_binding};
    use crate::session::Outbox;

    let root = Root::new("relay");
    let table = Arc::new(SpellTable::create(vec![SpellMetadata::new(42, "Test", 42)]).unwrap());
    let mut state = in_world_with(Some(table));
    let mut me = [0u8; 16];
    me[3] = 5;
    state
        .plugin_services
        .peers
        .configure_as(&root.0, InstanceId(me), &["healer".to_owned()]);
    let mut outbox = Outbox::new();
    let landed_on = |spell_id: u32| RemoteCast {
        caster_id: 30,
        target_id: 0x5000_0012,
        spell_id,
        effective_skill: 300,
        seconds_remaining: 60.0,
        landed: true,
    };
    with_binding(&mut state, &mut outbox, |b| {
        assert!(call::<calls::PeersIsConnected>(b, &()));
        assert_eq!(call::<calls::PeersSubscribe>(b, &7), None);
        assert!(!call::<calls::PeersUnsubscribe>(b, &1));
        let success = CastAnnouncement {
            target_id: 0x5000_0012,
            spell_id: 42,
            effective_skill: 300,
            duration_seconds: 60.0,
            landed: true,
        };
        assert!(call::<calls::PeersAnnounceCast>(b, &success));
        let own = call::<calls::PeersOwnCasts>(b, &0);
        assert_eq!(own.len(), 1);
        assert_eq!((own[0].caster_id, own[0].sequence), (PLAYER, 1));
        assert!(!own[0].is_remote);

        let mut gamma = client(0, 30, "Gamma", &["healer"]);
        gamma.world_name = "Local".into();
        assert!(call::<calls::PeersImportClient>(b, &gamma));
        let listed = call::<calls::PeersClients>(b, &());
        assert_eq!(listed.len(), 1);
        assert!(listed[0].is_remote);
        // A spell this client's table cannot name is refused.
        assert!(!call::<calls::PeersImportCast>(b, &landed_on(43)));
        assert!(call::<calls::PeersImportCast>(b, &landed_on(42)));
        let casts = call::<calls::PeersCasts>(b, &0);
        assert_eq!(casts.len(), 1);
        assert!(casts[0].is_remote && casts[0].caster_id == 30);
        let heal = RemoteBroadcast {
            sender_id: 30,
            line: "/heal me".into(),
            tags: vec!["healer".into()],
            delay_ms: 0,
        };
        assert!(call::<calls::PeersImportCommand>(b, &heal));
    });
    let character = ac_plugin::abi::CharacterInfo {
        id: PLAYER,
        name: "+Horan".into(),
        world: "Local".into(),
        ..ac_plugin::abi::CharacterInfo::default()
    };
    let now = CLOCKS.wall_unix_ms;
    assert_eq!(
        peer_link::tick(&mut state, Some(&character), true, 0.3, now),
        ["/heal me"]
    );
    // The character leaves: what it announced is not what the next says.
    peer_link::tick(&mut state, None, false, 0.3, now + 300);
    with_binding(&mut state, &mut outbox, |b| {
        assert!(call::<calls::PeersOwnCasts>(b, &0).is_empty());
    });
}
