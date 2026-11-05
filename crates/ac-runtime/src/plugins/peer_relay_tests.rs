//! A plugin with a transport of its own carries the peers between
//! computers: it reads what this client tells its neighbours and imports
//! what arrives from elsewhere, held to exactly the rules a neighbour's note
//! is read under. Cases and golden values from OpenAC (origin/main)
//! `tests/AcDream.Runtime.Tests/Plugins/LocalPluginPeerRelayTests.cs`,
//! named per test.

use std::path::{Path, PathBuf};

use ac_plugin::abi::peers::NetworkClient;
use ac_plugin::abi::world::Position;

use super::peers::{
    InstanceId, LocalCast, LocalCommand, MAXIMUM_REMOTE_CLIENTS, PeerRegistry, STALE_AFTER_MS,
};

const OWN: u32 = 10;
const REMOTE: u32 = 30;
/// 2026-09-25 12:00 UTC.
const START: i64 = 1_790_337_600_000;

struct Root(PathBuf);

impl Root {
    fn new(tag: &str) -> Self {
        let root =
            std::env::temp_dir().join(format!("openac-relay-5e2b-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        Self(root)
    }
}

impl Drop for Root {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Registry `which`, running as GUID `{which:08x}-0000-...`.
fn registry(root: &Path, which: u32) -> PeerRegistry {
    let mut bytes = [0u8; 16];
    bytes[..4].copy_from_slice(&which.to_be_bytes());
    PeerRegistry::new(root, InstanceId(bytes))
}

fn client(client_id: u32, player_id: u32, name: &str) -> NetworkClient {
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
        tags: vec!["healer".into()],
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

fn cast(caster: u32, target: u32, spell: u32, skill: i32, seconds: f64, landed: bool) -> LocalCast {
    LocalCast {
        caster_id: caster,
        target_id: target,
        spell_id: spell,
        effective_skill: skill,
        duration_seconds: seconds,
        landed,
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

// OpenAC `AnImportedPeerIsListedAsRemoteKeepsItsIdAndGoesStale`.
#[test]
fn an_imported_peer_is_listed_as_remote_keeps_its_id_and_goes_stale() {
    let root = Root::new("listed");
    let mut reader = registry(&root.0, 1);
    let mut now = START;

    assert!(reader.import_remote_client(&client(999, REMOTE, "Gamma"), OWN, now));
    let listed = reader.remote_clients(OWN, now);
    assert_eq!(listed.len(), 1);
    let imported = &listed[0];
    assert!(imported.is_remote);
    assert_eq!(imported.player_id, REMOTE);
    assert_eq!(imported.name, "Gamma");
    assert_eq!(imported.tags, ["healer"]);
    assert_eq!(imported.current_health, 90);
    // The relay's own number means nothing here.
    assert_ne!(imported.client_id, 999);
    assert_ne!(imported.client_id, 0);
    assert_ne!(imported.client_id, reader.client_id());

    now += 10_000;
    let mut weaker = client(0, REMOTE, "Gamma");
    weaker.current_health = 40;
    assert!(reader.import_remote_client(&weaker, OWN, now));
    let refreshed = reader.remote_clients(OWN, now);
    assert_eq!(refreshed.len(), 1);
    assert_eq!(refreshed[0].client_id, imported.client_id);
    assert_eq!(refreshed[0].current_health, 40);

    now += STALE_AFTER_MS + 1;
    assert!(reader.remote_clients(OWN, now).is_empty());
}

// OpenAC `AnImportIsRefusedWhereANoteWouldBe`.
#[test]
fn an_import_is_refused_where_a_note_would_be() {
    let root = Root::new("refused");
    let mut reader = registry(&root.0, 1);
    let good = client(0, REMOTE, "Gamma");
    let with = |change: &dyn Fn(&mut NetworkClient)| {
        let mut c = good.clone();
        change(&mut c);
        c
    };
    for bad in [
        with(&|c| c.player_id = 0),
        with(&|c| c.player_id = OWN),
        with(&|c| c.name = " ".into()),
        with(&|c| c.name = "n".repeat(129)),
        with(&|c| c.world_name = "w".repeat(129)),
        with(&|c| c.heading = f32::NAN),
        with(&|c| c.position.east_west = f64::INFINITY),
        with(&|c| c.tags = vec!["t".repeat(65)]),
    ] {
        assert!(!reader.import_remote_client(&bad, OWN, START), "{bad:?}");
    }
    assert!(reader.remote_clients(OWN, START).is_empty());

    // A cast or a line from somebody never imported is refused.
    assert!(!reader.import_remote_cast(&cast(REMOTE, 0x5000_0012, 42, 300, 30.0, true), START));
    assert!(!reader.import_remote_command(&line(REMOTE, &[], "/go", 0), START));
}

// OpenAC `TheImportCapCountsOnlyPeersThatAreStillRecent`.
#[test]
fn the_import_cap_counts_only_peers_that_are_still_recent() {
    let root = Root::new("cap");
    let mut reader = registry(&root.0, 1);
    for index in 0..u32::try_from(MAXIMUM_REMOTE_CLIENTS).unwrap() {
        let peer = client(0, 1000 + index, &format!("P{index}"));
        assert!(reader.import_remote_client(&peer, OWN, START));
    }
    assert!(!reader.import_remote_client(&client(0, 5000, "Late"), OWN, START));
    // Refreshing one already held is not a new peer.
    assert!(reader.import_remote_client(&client(0, 1000, "P0"), OWN, START));

    let later = START + STALE_AFTER_MS + 1_000;
    assert!(reader.import_remote_client(&client(0, 5000, "Late"), OWN, later));
    let listed = reader.remote_clients(OWN, later);
    assert_eq!(listed.len(), 1);
    assert_eq!(listed[0].name, "Late");
}

// OpenAC `ACharacterSeenOnThisComputerIsNotAlsoListedAsRemote`.
#[test]
fn a_character_seen_on_this_computer_is_not_also_listed_as_remote() {
    let root = Root::new("shadow");
    let mut reader = registry(&root.0, 1);
    let mut neighbour = registry(&root.0, 2);
    assert!(neighbour.record_cast(&cast(REMOTE, 0x5000_0012, 42, 300, 60.0, true), START));
    neighbour.publish(&client(neighbour.client_id(), REMOTE, "Gamma"), START);

    assert!(reader.import_remote_client(&client(0, REMOTE, "Gamma"), OWN, START));
    assert!(reader.import_remote_cast(&cast(REMOTE, 0x5000_0013, 43, 300, 60.0, true), START));

    let listed = reader.remote_clients(OWN, START);
    assert_eq!(listed.len(), 1);
    assert!(!listed[0].is_remote);
    assert_eq!(listed[0].client_id, neighbour.client_id());
    let casts = reader.remote_casts(0, "Coldeve", OWN, START);
    assert_eq!(casts.len(), 1);
    assert!(!casts[0].is_remote);
    assert_eq!(casts[0].spell_id, 42);
}

// OpenAC `WhatAHiddenCopyCarriedIsNotReDeliveredWhenTheNoteGoesAway`.
#[test]
fn what_a_hidden_copy_carried_is_not_re_delivered_when_the_note_goes_away() {
    let root = Root::new("hidden");
    let mut reader = registry(&root.0, 1);
    let mut neighbour = registry(&root.0, 2);
    assert!(neighbour.record_cast(&cast(REMOTE, 0x5000_0012, 42, 300, 60.0, true), START));
    assert!(neighbour.record_command(&line(REMOTE, &[], "/go", 0), START));
    neighbour.publish(&client(neighbour.client_id(), REMOTE, "Gamma"), START);

    // The relay's echo of the same cast and line.
    assert!(reader.import_remote_client(&client(0, REMOTE, "Gamma"), OWN, START));
    assert!(reader.import_remote_cast(&cast(REMOTE, 0x5000_0012, 42, 300, 60.0, true), START));
    assert!(reader.import_remote_command(&line(REMOTE, &[], "/go", 0), START));

    let casts = reader.remote_casts(0, "Coldeve", OWN, START);
    assert_eq!(casts.len(), 1);
    assert!(!casts[0].is_remote);
    let lines = reader.remote_commands(0, "Coldeve", OWN, &[], START);
    assert_eq!(lines.len(), 1);
    assert!(!lines[0].command.is_remote);

    neighbour.withdraw();
    let now = START + 1_000;
    assert!(reader.import_remote_client(&client(0, REMOTE, "Gamma"), OWN, now));

    let listed = reader.remote_clients(OWN, now);
    assert_eq!(listed.len(), 1);
    assert!(listed[0].is_remote);
    assert!(
        reader
            .remote_casts(casts[0].sequence, "Coldeve", OWN, now)
            .is_empty()
    );
    assert!(
        reader
            .remote_commands(lines[0].command.sequence, "Coldeve", OWN, &[], now)
            .is_empty()
    );

    // What the relay carries from now on is taken in as before.
    assert!(reader.import_remote_cast(&cast(REMOTE, 0x5000_0012, 44, 300, 60.0, true), now));
    let next = reader.remote_casts(casts[0].sequence, "Coldeve", OWN, now);
    assert_eq!(next.len(), 1);
    assert!(next[0].is_remote);
    assert_eq!(next[0].spell_id, 44);
}

// OpenAC `ANoteThatShowsAfterItsRelayedCopyStartsAtTheEndOfItsRings`.
#[test]
fn a_note_that_shows_after_its_relayed_copy_starts_at_the_end_of_its_rings() {
    let root = Root::new("late-note");
    let mut reader = registry(&root.0, 1);
    assert!(reader.import_remote_client(&client(0, REMOTE, "Gamma"), OWN, START));
    assert!(reader.import_remote_cast(&cast(REMOTE, 0x5000_0012, 42, 300, 60.0, true), START));
    assert!(reader.import_remote_command(&line(REMOTE, &[], "/go", 0), START));
    let relayed = reader.remote_casts(0, "Coldeve", OWN, START);
    assert_eq!(relayed.len(), 1);
    let relayed_lines = reader.remote_commands(0, "Coldeve", OWN, &[], START);
    assert_eq!(relayed_lines.len(), 1);

    let mut neighbour = registry(&root.0, 2);
    assert!(neighbour.record_cast(&cast(REMOTE, 0x5000_0012, 42, 300, 60.0, true), START));
    assert!(neighbour.record_command(&line(REMOTE, &[], "/go", 0), START));
    neighbour.publish(&client(neighbour.client_id(), REMOTE, "Gamma"), START);

    let listed = reader.remote_clients(OWN, START);
    assert_eq!(listed.len(), 1);
    assert!(!listed[0].is_remote);
    assert!(
        reader
            .remote_casts(relayed[0].sequence, "Coldeve", OWN, START)
            .is_empty()
    );
    assert!(
        reader
            .remote_commands(
                relayed_lines[0].command.sequence,
                "Coldeve",
                OWN,
                &[],
                START
            )
            .is_empty()
    );

    let now = START + 1_000;
    assert!(neighbour.record_cast(&cast(REMOTE, 0x5000_0012, 45, 300, 60.0, true), now));
    neighbour.publish(&client(neighbour.client_id(), REMOTE, "Gamma"), now);
    let fresh = reader.remote_casts(relayed[0].sequence, "Coldeve", OWN, now);
    assert_eq!(fresh.len(), 1);
    assert!(!fresh[0].is_remote);
    assert_eq!(fresh[0].spell_id, 45);
}

// OpenAC `AnImportedCastIsReadOnceMarkedRemoteAndCountsDown`.
#[test]
fn an_imported_cast_is_read_once_marked_remote_and_counts_down() {
    let root = Root::new("countdown");
    let mut reader = registry(&root.0, 1);
    assert!(reader.import_remote_client(&client(0, REMOTE, "Gamma"), OWN, START));
    assert!(reader.import_remote_cast(&cast(REMOTE, 0x5000_0012, 42, 357, 30.0, true), START));
    // An attempt carries no duration, whatever the relay said.
    assert!(reader.import_remote_cast(&cast(REMOTE, 0x5000_0012, 43, 0, 99.0, false), START));
    // Well-formedness is the note rule.
    for bad in [
        cast(REMOTE, 0, 42, 357, 30.0, true),
        cast(REMOTE, 0x5000_0012, 42, -1, 30.0, true),
        cast(REMOTE, 0x5000_0012, 42, 357, 0.0, true),
        cast(REMOTE, 0x5000_0012, 42, 357, f64::NAN, true),
    ] {
        assert!(!reader.import_remote_cast(&bad, START), "{bad:?}");
    }
    // A peer playing in another world names other creatures entirely.
    let mut elsewhere = client(0, REMOTE + 1, "Elsewhere");
    elsewhere.world_name = "Frostfell".into();
    assert!(reader.import_remote_client(&elsewhere, OWN, START));
    assert!(reader.import_remote_cast(&cast(REMOTE + 1, 0x5000_0012, 44, 357, 30.0, true), START));

    let now = START + 5_000;
    let casts = reader.remote_casts(0, "Coldeve", OWN, now);
    assert_eq!(casts.len(), 2);
    let peers = reader.remote_clients(OWN, now);
    let peer = peers.iter().find(|c| c.player_id == REMOTE).unwrap();
    assert!(
        casts
            .iter()
            .all(|c| c.is_remote && c.client_id == peer.client_id)
    );
    assert_eq!(casts[0].caster_id, REMOTE);
    assert!(casts[0].landed);
    assert!((casts[0].seconds_remaining - 25.0).abs() < 1e-3);
    assert!(!casts[1].landed);
    assert!(casts[1].seconds_remaining.abs() < f64::EPSILON);
    assert!(
        reader
            .remote_casts(casts[1].sequence, "Coldeve", OWN, now)
            .is_empty()
    );
}

// OpenAC `AnImportedCastIsRefusedOnceItsCasterHasGoneStale`.
#[test]
fn an_imported_cast_is_refused_once_its_caster_has_gone_stale() {
    let root = Root::new("stale-caster");
    let mut reader = registry(&root.0, 1);
    assert!(reader.import_remote_client(&client(0, REMOTE, "Gamma"), OWN, START));
    let later = START + STALE_AFTER_MS + 1;
    assert!(!reader.import_remote_cast(&cast(REMOTE, 0x5000_0012, 42, 357, 30.0, true), later));
    assert!(!reader.import_remote_command(&line(REMOTE, &[], "/go", 0), later));
}

// OpenAC `AnImportedLineReachesOnlyTheLabelsItIsAimedAt`: the reader has
// the highest client id there is, so an imported peer would rank ahead if
// imported peers were counted in the stagger order.
#[test]
fn an_imported_line_reaches_only_the_labels_it_is_aimed_at() {
    let root = Root::new("aimed");
    let mut reader = registry(&root.0, 0xFFFF_FFFF);
    assert!(reader.import_remote_client(&client(0, REMOTE, "Gamma"), OWN, START));
    assert!(reader.import_remote_client(&client(0, REMOTE + 1, "Delta"), OWN, START));
    assert!(reader.import_remote_command(&line(REMOTE, &["healer"], "/heal me", 200), START));
    assert!(reader.import_remote_command(&line(REMOTE, &["tank"], "/taunt", 0), START));
    assert!(!reader.import_remote_command(&line(REMOTE, &[], "bad\nline", 0), START));
    assert!(!reader.import_remote_command(&line(REMOTE, &[], "/go", -1), START));

    let taken = reader.remote_commands(0, "Coldeve", OWN, &["healer".to_owned()], START);
    assert_eq!(taken.len(), 1);
    let only = &taken[0];
    assert!(only.command.is_remote);
    assert_eq!(only.command.line, "/heal me");
    assert_eq!(only.command.sender_id, REMOTE);
    assert_eq!(only.command.tags, ["healer"]);
    // No neighbour on this computer ranks ahead: one delay.
    assert_eq!(only.stagger_ms, 200);
}

// OpenAC `OwnAnnouncementsAreReadBackInOrderUntilTheyAgeOut`.
#[test]
fn own_announcements_are_read_back_in_order_until_they_age_out() {
    let root = Root::new("own");
    let mut own = registry(&root.0, 1);
    assert!(own.record_cast(&cast(OWN, 0x5000_0012, 42, 357, 0.0, false), START));
    assert!(own.record_cast(&cast(OWN, 0x5000_0012, 42, 357, 60.0, true), START));
    assert!(own.record_command(&line(OWN, &["squad"], "/go", 100), START));

    let now = START + 4_000;
    let casts = own.capture_own_casts(0, now);
    assert_eq!(casts.iter().map(|c| c.sequence).collect::<Vec<_>>(), [1, 2]);
    assert!(
        casts
            .iter()
            .all(|c| c.client_id == own.client_id() && !c.is_remote)
    );
    assert!(!casts[0].landed);
    assert!(casts[0].seconds_remaining.abs() < f64::EPSILON);
    assert!(casts[1].landed);
    assert!((casts[1].seconds_remaining - 56.0).abs() < 1e-3);
    assert_eq!(own.capture_own_casts(1, now).len(), 1);
    assert!(own.capture_own_casts(2, now).is_empty());

    let commands = own.capture_own_commands(0, now);
    assert_eq!(commands.len(), 1);
    let command = &commands[0];
    assert_eq!(command.sequence, 1);
    assert_eq!(command.client_id, own.client_id());
    assert_eq!(command.sender_id, OWN);
    assert_eq!(command.line, "/go");
    assert_eq!(command.tags, ["squad"]);
    assert_eq!(command.sent_unix_ms, START);

    let aged = now + STALE_AFTER_MS;
    assert!(own.capture_own_casts(0, aged).is_empty());
    assert!(own.capture_own_commands(0, aged).is_empty());
}

// OpenAC `ForgottenAnnouncementsAreNotReadBackAndTheNumberingCarriesOn`.
#[test]
fn forgotten_announcements_are_not_read_back_and_the_numbering_carries_on() {
    let root = Root::new("forget");
    let mut own = registry(&root.0, 1);
    assert!(own.record_cast(&cast(OWN, 0x5000_0012, 42, 357, 60.0, true), START));
    assert!(own.record_command(&line(OWN, &[], "/go", 0), START));

    own.forget_own_announcements();

    assert!(own.capture_own_casts(0, START).is_empty());
    assert!(own.capture_own_commands(0, START).is_empty());
    assert!(own.record_cast(&cast(OWN + 1, 0x5000_0012, 43, 357, 60.0, true), START));
    let next = own.capture_own_casts(0, START);
    assert_eq!(next.len(), 1);
    assert_eq!(next[0].sequence, 2);
    assert_eq!(next[0].caster_id, OWN + 1);
}
