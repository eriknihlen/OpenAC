//! The other clients on this computer, on both arms (OpenAC
//! `Scenarios/PeerAnnouncementParityTests.cs`,
//! `PeerCastSharingParityTests.cs`, `PeerCommandParityTests.cs`,
//! `PeerRelayParityTests.cs`): what a client tells the others about itself,
//! the casts and lines it shares, the ones it reads from a neighbour, and
//! what a relay plugin carries to other computers.
//!
//! As in OpenAC's harness (`ParityPeerHub`, `ParityPeerClient`), both arms
//! talk to a real peer hub on the arm's peer folder, started before the arm
//! subscribes, and the neighbour is a second client's transport and
//! in-memory registry on the same hub; no note file is involved. A line run
//! on the arm is seen through a copy of the relay answering the scenario's
//! verb, which logs what it was handed.
//!
//! A client's record carries where the character stands, which is its
//! body's place: OpenAC's harness always lays a body down, and here a body
//! needs the retail dats, so these scenarios run with them.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use ac_core::spells::{SpellComponentRequirements, SpellMetadata, SpellTable};
use ac_plugin::abi::calls;
use ac_plugin::abi::peers::{
    Broadcast, CastAnnouncement, NetworkClient, PeerCast, RemoteBroadcast, RemoteCast,
};
use ac_runtime::gameplay::GameTables;
use ac_runtime::plugins::peer_transport::{PeerTransport, StartHub, endpoint_for};
use ac_runtime::plugins::peer_wire::{CASTS, CLIENT_STATE, COMMANDS};
use ac_runtime::plugins::peers::{InstanceId, LocalCast, LocalCommand, PeerRegistry};

use crate::host_parity::arm::{Arm, CONFIGURED_PLUGIN_TAGS, Setup, run_with};
use crate::host_parity::scenarios::chat_entry::{TALK_ACTION, said};
use crate::host_parity::scenarios::item::one;
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

/// The spell the arms know, so a cast has something to name.
const SHARED_SPELL: u32 = 42;
/// The character the neighbour plays.
const OTHER_CHARACTER: u32 = 0x5000_0002;
/// The character the onlooker plays at first (OpenAC `ParityPeerClient`).
const ONLOOKER: u32 = 0x5000_0003;
/// The character on another computer a relay imports.
const REMOTE_CHARACTER: u32 = 0x5000_0044;
/// What the server says each pool is at.
const HEALTH_LEFT: u32 = 55;
const STAMINA_LEFT: u32 = 42;
const MANA_LEFT: u32 = 40;
/// The client action `@permit add` becomes.
const PERMIT_ACTION: u32 = 0x0219;

/// The verbs the arms answer, so a delivered line has somewhere to land.
const ECHO: &str = "openac.parityecho";
const RELAY: &str = "openac.parityrelay";

fn now_ms() -> i64 {
    i64::try_from(
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_millis(),
    )
    .unwrap()
}

/// The arms' setup: the shared spell in their table, the verbs installed,
/// and the pools as the server states them on arrival.
fn setup() -> Setup {
    let spell = SpellMetadata {
        school: "Life Magic".into(),
        generation: 7,
        difficulty: 350,
        ..SpellMetadata::new(SHARED_SPELL, "Fire Vulnerability Other VII", 0)
    };
    let tables = GameTables::from_parts(
        SpellTable::create([spell]).unwrap(),
        SpellComponentRequirements::default(),
        HashMap::new(),
    );
    Setup {
        tables: Some(Arc::new(tables)),
        more_plugins: vec![ECHO, RELAY],
        description: wire::Description {
            // Health, stamina and mana: ranks, start, xp, now.
            attributes: vec![
                (7, 3, 100, 0, HEALTH_LEFT),
                (8, 9, 100, 0, STAMINA_LEFT),
                (9, 1, 100, 0, MANA_LEFT),
            ],
            ..wire::Description::default()
        },
        dats: true,
        ..Setup::default()
    }
}

/// OpenAC `ParityPeerHub`: a hub on the arm's peer folder, listening before
/// anything connects (a refused first connect would ask for a hub to be
/// started), and the arm subscribed to client state, casts and commands.
fn start_hub(arm: &mut Arm) {
    let endpoint = endpoint_for(&arm.peer_directory());
    let served = endpoint.clone();
    std::thread::spawn(move || {
        let _ = ac_runtime::plugins::peer_hub::serve(&served, |_| {});
    });
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if let Ok(probe) = endpoint.connect(Duration::from_millis(100)) {
            probe.shutdown();
            break;
        }
        assert!(
            Instant::now() < deadline,
            "the parity peer hub did not start listening"
        );
        std::thread::sleep(Duration::from_millis(1));
    }
    let lease = arm.call::<calls::PeersSubscribe>(&(CLIENT_STATE | CASTS | COMMANDS));
    assert!(lease.is_some(), "{} handed out no lease", arm.name());
    arm.wait(Duration::from_millis(20));
}

fn no_start() -> StartHub {
    Arc::new(|| Err(std::io::Error::other("the parity hub should be listening")))
}

/// Another client on this computer (OpenAC `ParityPeerClient`): its own
/// transport and in-memory registry on the arm's hub, with a fixed identity
/// so both arms read the same client id; casts and lines it records go out
/// with its next note.
struct Neighbour {
    registry: PeerRegistry,
    transport: PeerTransport,
    casts: Vec<(LocalCast, i64)>,
    commands: Vec<LocalCommand>,
}

impl Neighbour {
    fn new(arm: &mut Arm, last: u8) -> Self {
        let mut id = [0u8; 16];
        id[..4].copy_from_slice(&[0x7e, 0xa5, 0x00, 0x00]);
        id[15] = last;
        let instance = InstanceId(id);
        let directory = arm.peer_directory();
        let mut registry = PeerRegistry::in_memory(&directory, instance);
        let mut transport = PeerTransport::new(endpoint_for(&directory), instance, no_start());
        transport
            .subscribe(CLIENT_STATE | CASTS | COMMANDS, &mut registry)
            .expect("a lease");
        let mut neighbour = Self {
            registry,
            transport,
            casts: Vec::new(),
            commands: Vec::new(),
        };
        let mut note = neighbour_note(arm, ONLOOKER, 100);
        note.tags = vec!["squad".to_owned()];
        neighbour.publish(arm, &note);
        neighbour
    }

    fn client_id(&self) -> u32 {
        self.registry.client_id()
    }

    /// Takes in what the hub sent, a line as the client's own delivery
    /// does.
    fn pump(&mut self) {
        for command in self.transport.pump(&mut self.registry, now_ms()) {
            self.registry.push_command(
                &command.sender,
                &command.line,
                &command.tags,
                command.delay_ms,
                command.sent_unix_ms,
            );
        }
    }

    /// Pumps both clients until `found` gives something.
    fn until<T>(
        &mut self,
        arm: &mut Arm,
        what: &str,
        mut found: impl FnMut(&mut Self, &mut Arm) -> Option<T>,
    ) -> T {
        let deadline = Instant::now() + Duration::from_secs(12);
        loop {
            self.pump();
            if let Some(value) = found(self, arm) {
                return value;
            }
            assert!(
                Instant::now() < deadline,
                "{}: timed out waiting for {what}",
                arm.name()
            );
            arm.wait(Duration::from_millis(5));
        }
    }

    fn record_cast(&mut self, cast: LocalCast, at_unix_ms: i64) {
        self.casts.push((cast, at_unix_ms));
    }

    fn record_command(&mut self, command: LocalCommand) {
        self.commands.push(command);
    }

    /// Tells the hub who this client is and waits until both sides see it;
    /// then the recorded casts (with what is left of each) and lines go
    /// out, and the arm has them before this returns.
    fn publish(&mut self, arm: &mut Arm, note: &NetworkClient) {
        let client_id = self.client_id();
        let note = NetworkClient {
            client_id,
            ..note.clone()
        };
        self.transport.update_self(note.clone(), &mut self.registry);
        self.until(arm, "the neighbour registered", |n, arm| {
            (!n.registry
                .remote_clients(note.player_id, now_ms())
                .is_empty()
                && n.transport.demand() & CASTS != 0
                && arm
                    .call::<calls::PeersClients>(&())
                    .iter()
                    .any(|c| c.client_id == client_id && c.player_id == note.player_id))
            .then_some(())
        });
        let casts = std::mem::take(&mut self.casts);
        for (sequence, (cast, at)) in casts.iter().enumerate() {
            #[allow(clippy::cast_precision_loss)]
            let elapsed = (now_ms() - at) as f64 / 1000.0;
            assert!(self.transport.send_cast(PeerCast {
                sequence: i64::try_from(sequence).unwrap() + 1,
                client_id,
                caster_id: cast.caster_id,
                target_id: cast.target_id,
                spell_id: cast.spell_id,
                effective_skill: cast.effective_skill,
                seconds_remaining: (cast.duration_seconds - elapsed).max(0.0),
                landed: cast.landed,
                is_remote: false,
            }));
        }
        if !casts.is_empty() {
            self.until(arm, "the casts arrived", |_, arm| {
                (arm.call::<calls::PeersCasts>(&0).len() >= casts.len()).then_some(())
            });
        }
        let commands = std::mem::take(&mut self.commands);
        for command in &commands {
            assert!(self.transport.send_command(
                &command.line,
                command.tags.clone(),
                command.delay_ms,
                now_ms(),
                &mut self.registry,
            ));
        }
        if !commands.is_empty() {
            // A later state is ordered after every line on this connection.
            let delivered = NetworkClient {
                name: "Delivered".to_owned(),
                ..note
            };
            self.transport.update_self(delivered, &mut self.registry);
            self.until(arm, "the lines arrived", |_, arm| {
                arm.call::<calls::PeersClients>(&())
                    .iter()
                    .any(|c| c.client_id == client_id && c.name == "Delivered")
                    .then_some(())
            });
            arm.wait(Duration::from_millis(20));
        }
    }
}

impl Drop for Neighbour {
    fn drop(&mut self) {
        self.transport.dispose(&mut self.registry);
    }
}

/// What the neighbour says about itself: this client's world and place.
fn neighbour_note(arm: &mut Arm, player: u32, health: u32) -> NetworkClient {
    let me = arm
        .call::<calls::PeersCaptureSelf>(&())
        .unwrap_or_else(|| panic!("{} says nothing about itself", arm.name()));
    NetworkClient {
        client_id: 0,
        player_id: player,
        name: "Onlooker".to_owned(),
        world_name: me.world_name,
        position: me.position,
        tags: Vec::new(),
        current_health: health,
        current_mana: 100,
        current_stamina: 100,
        max_health: 100,
        max_mana: 100,
        max_stamina: 100,
        heading: 0.0,
        is_remote: false,
    }
}

/// The lines the relay copy answering `plugin`'s verb was handed.
fn ran(arm: &Arm, plugin: &str) -> Vec<String> {
    arm.logs
        .iter()
        .filter(|(p, _)| p == plugin)
        .filter_map(|(_, m)| m.strip_prefix("parity unknown "))
        .map(str::to_owned)
        .collect()
}

/// Pumps the arm (and the neighbour) until `found` gives something.
fn read_until<T>(arm: &mut Arm, what: &str, mut found: impl FnMut(&mut Arm) -> Option<T>) -> T {
    let deadline = Instant::now() + Duration::from_secs(12);
    loop {
        if let Some(value) = found(arm) {
            return value;
        }
        assert!(
            Instant::now() < deadline,
            "{}: timed out waiting for {what}",
            arm.name()
        );
        arm.wait(Duration::from_millis(20));
    }
}

fn record_cast(transcript: &mut Transcript, cast: &PeerCast) {
    transcript.record("sequence", cast.sequence);
    transcript.record("clientIdIsSet", cast.client_id != 0);
    transcript.record_id("caster", cast.caster_id);
    transcript.record_id("target", cast.target_id);
    transcript.record("spellId", cast.spell_id);
    transcript.record("effectiveSkill", cast.effective_skill);
    transcript.record("landed", cast.landed);
    transcript.record(
        "secondsRemainingWithinTheDuration",
        cast.seconds_remaining > 0.0 && cast.seconds_remaining <= 60.0,
    );
}

/// The arm as the neighbour sees it, once it carries what `ready` wants.
fn the_arm(
    onlooker: &mut Neighbour,
    arm: &mut Arm,
    ready: impl Fn(&NetworkClient) -> bool,
) -> NetworkClient {
    onlooker.until(arm, "the arm's record", |n, _| {
        n.registry
            .remote_clients(ONLOOKER, now_ms())
            .into_iter()
            .find(|c| c.player_id == world::PLAYER && ready(c))
    })
}

// OpenAC `PeerAnnouncementParityTests.EitherClientAnnouncesItselfToTheMachine`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_announces_itself_to_the_machine() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        let mut onlooker = Neighbour::new(arm, 0xc6);
        // The record read is the one that carries what the server said.
        let announced = the_arm(&mut onlooker, arm, |c| c.current_health == HEALTH_LEFT);

        transcript.step("announced");
        transcript.record_id("playerId", announced.player_id);
        transcript.record("name", &announced.name);
        transcript.record("worldName", &announced.world_name);
        transcript.record("health", announced.current_health);
        transcript.record("maxHealth", announced.max_health);
        transcript.record("stamina", announced.current_stamina);
        transcript.record("mana", announced.current_mana);
        assert_eq!(announced.name, world::PLAYER_NAME);
        assert_eq!(announced.world_name, world::WORLD_NAME);
        assert_eq!(
            (
                announced.current_health,
                announced.current_stamina,
                announced.current_mana
            ),
            (HEALTH_LEFT, STAMINA_LEFT, MANA_LEFT),
            "{}",
            arm.name()
        );
        assert!(announced.max_health >= announced.current_health && announced.max_health > 0);
    });
}

// OpenAC `PeerAnnouncementParityTests.EitherClientAnnouncesItsConfiguredWords`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_announces_its_configured_words() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        let mut onlooker = Neighbour::new(arm, 0xc6);
        let announced = the_arm(&mut onlooker, arm, |_| true);
        transcript.step("words");
        transcript.record("words", &announced.tags);
        assert_eq!(announced.tags, CONFIGURED_PLUGIN_TAGS, "{}", arm.name());
    });
}

// OpenAC `PeerCastSharingParityTests.EitherClientTellsTheMachineWhatItCast`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_tells_the_machine_what_it_cast() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        let mut onlooker = Neighbour::new(arm, 0xc7);
        transcript.step("announced");
        let accepted = arm.call::<calls::PeersAnnounceCast>(&CastAnnouncement {
            target_id: world::MONSTER,
            spell_id: SHARED_SPELL,
            effective_skill: 357,
            duration_seconds: 60.0,
            landed: true,
        });
        transcript.record("accepted", accepted);
        assert!(accepted, "{}", arm.name());
        let seen = onlooker.until(arm, "the arm's cast", |n, _| {
            let casts = n
                .registry
                .remote_casts(0, world::WORLD_NAME, ONLOOKER, now_ms());
            (!casts.is_empty()).then_some(casts)
        });
        assert_eq!(seen.len(), 1, "{}: {seen:?}", arm.name());
        record_cast(transcript, &seen[0]);
        assert_eq!(seen[0].caster_id, world::PLAYER);
        assert_eq!(seen[0].target_id, world::MONSTER);
        assert_eq!(seen[0].spell_id, SHARED_SPELL);
        assert_eq!(seen[0].effective_skill, 357);
        assert!(seen[0].landed);
    });
}

// OpenAC `PeerCastSharingParityTests.EitherClientReadsWhatAnotherClientOnTheMachineCast`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_reads_what_another_client_on_the_machine_cast() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        // The neighbour cast five seconds ago: a reader is handed what is
        // left of the effect, not the duration published.
        let mut other = Neighbour::new(arm, 0xc7);
        let before = Instant::now();
        other.record_cast(
            LocalCast {
                caster_id: OTHER_CHARACTER,
                target_id: world::MONSTER,
                spell_id: SHARED_SPELL,
                effective_skill: 357,
                duration_seconds: 60.0,
                landed: true,
            },
            now_ms() - 5_000,
        );
        let note = neighbour_note(arm, OTHER_CHARACTER, 100);
        other.publish(arm, &note);

        transcript.step("read");
        let read = arm.call::<calls::PeersCasts>(&0);
        let elapsed = before.elapsed().as_secs_f64();
        assert_eq!(
            read.len(),
            1,
            "{} did not read the cast: {read:?}",
            arm.name()
        );
        record_cast(transcript, &read[0]);
        assert_eq!(read[0].client_id, other.client_id());
        let minimum = (55.0 - elapsed - 0.002).max(0.0);
        let fair = read[0].seconds_remaining >= minimum && read[0].seconds_remaining <= 55.002;
        transcript.record("fiveSecondsHaveComeOffTheSixtyPublished", fair);
        assert!(fair, "{}: {} s left", arm.name(), read[0].seconds_remaining);

        // A plugin's own cursor: from where it left off, nothing.
        let after = arm.call::<calls::PeersCasts>(&read[0].sequence);
        transcript.record("afterTheCursor", after.len());
        assert!(after.is_empty());
        // The host did not decide that a cast read is an effect it tracks.
        let tracked = arm.call::<calls::EnchantmentsCapture>(&world::MONSTER);
        transcript.record("trackedWithoutBeingAskedTo", tracked.len());
        assert!(tracked.is_empty());
    });
}

const ECHO_VERB: &str = "parityecho";

// OpenAC `PeerCommandParityTests.EitherClientTellsTheMachineWhatItAskedTheOthersToRun`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_tells_the_machine_what_it_asked_the_others_to_run() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        // The onlooker answers to "squad", so a line aimed there is its.
        let mut onlooker = Neighbour::new(arm, 0xc8);

        transcript.step("broadcast");
        let accepted = arm.call::<calls::PeersBroadcast>(&Broadcast {
            line: format!("/{ECHO_VERB} go"),
            tags: vec!["squad".to_owned()],
            delay_ms: 250,
        });
        transcript.record("accepted", accepted);
        assert!(accepted, "{}", arm.name());
        let squad = ["squad".to_owned()];
        let seen = onlooker.until(arm, "the arm's line", |n, _| {
            let lines =
                n.registry
                    .remote_commands(0, world::WORLD_NAME, ONLOOKER, &squad, now_ms());
            (!lines.is_empty()).then_some(lines)
        });
        assert_eq!(seen.len(), 1, "{}: {seen:?}", arm.name());
        let only = &seen[0].command;
        transcript.record("sequence", only.sequence);
        transcript.record("clientIdIsSet", only.client_id != 0);
        transcript.record_id("sender", only.sender_id);
        transcript.record("line", &only.line);
        transcript.record("tags", &only.tags);
        transcript.record("stagger", seen[0].stagger_ms);
        assert_eq!(only.sender_id, world::PLAYER);
        assert_eq!(only.line, format!("/{ECHO_VERB} go"));
        assert_eq!(only.tags, squad);
        assert_eq!(seen[0].stagger_ms, 250);
    });
}

// OpenAC `PeerCommandParityTests.EitherClientRunsALineAnotherClientOnTheMachineAsked`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_runs_a_line_another_client_on_the_machine_asked() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        let mut other = Neighbour::new(arm, 0xc8);
        other.record_command(LocalCommand {
            sender_id: ONLOOKER,
            tags: Vec::new(),
            line: format!("/{ECHO_VERB} go"),
            delay_ms: 0,
        });
        let note = neighbour_note(arm, ONLOOKER, 100);
        other.publish(arm, &note);

        transcript.step("run");
        read_until(arm, "the line run", |arm| {
            (!ran(arm, ECHO).is_empty()).then_some(())
        });
        let lines = ran(arm, ECHO);
        transcript.record("linesRun", &lines);
        assert_eq!(lines, ["Some(\"go\")"], "{}", arm.name());

        // The same line is there for a plugin to read, with its own cursor.
        let read = arm.call::<calls::PeersCommands>(&0);
        transcript.record("readByAPlugin", read.len());
        assert_eq!(read.len(), 1, "{}", arm.name());
        let after = arm.call::<calls::PeersCommands>(&read[0].sequence);
        transcript.record("afterTheCursor", after.len());
        assert!(after.is_empty());
        arm.sync();
        transcript.record("stillRunOnce", ran(arm, ECHO).len());
        assert_eq!(ran(arm, ECHO).len(), 1);
    });
}

// OpenAC `PeerCommandParityTests.EitherClientRunsABroadcastThatNoPluginVerbClaims`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_runs_a_broadcast_that_no_plugin_verb_claims() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        let mut other = Neighbour::new(arm, 0xc8);
        for line in ["@permit add Bob", "hello"] {
            other.record_command(LocalCommand {
                sender_id: ONLOOKER,
                tags: Vec::new(),
                line: line.to_owned(),
                delay_ms: 0,
            });
        }
        let _ = arm.outbound();
        let note = neighbour_note(arm, ONLOOKER, 100);
        other.publish(arm, &note);

        transcript.step("run");
        let mut sent = Vec::new();
        read_until(arm, "both lines sent", |arm| {
            sent.extend(arm.outbound());
            (sent.len() >= 2).then_some(())
        });
        transcript.record("sent", &sent);
        let permit = one(&sent, PERMIT_ACTION);
        assert_eq!(permit.text_at(12), "Bob", "{}", arm.name());
        assert_eq!(said(&one(&sent, TALK_ACTION)), "hello");
    });
}

// OpenAC `PeerCommandParityTests.EitherClientLetsAPluginChangeTheLabelsItAnswersTo`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_lets_a_plugin_change_the_labels_it_answers_to() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        transcript.step("relabelled");
        let accepted = arm.call::<calls::PeersSetTags>(&vec!["healer".to_owned()]);
        transcript.record("accepted", accepted);
        let mut onlooker = Neighbour::new(arm, 0xc8);
        let seen = the_arm(&mut onlooker, arm, |c| c.tags == ["healer"]);
        transcript.record("tags", &seen.tags);

        // One line for a label the arm no longer wears, one for its new one.
        for (tag, word) in [(CONFIGURED_PLUGIN_TAGS[0], "old"), ("healer", "new")] {
            onlooker.record_command(LocalCommand {
                sender_id: ONLOOKER,
                tags: vec![tag.to_owned()],
                line: format!("/{ECHO_VERB} {word}"),
                delay_ms: 0,
            });
        }
        let note = neighbour_note(arm, ONLOOKER, 100);
        onlooker.publish(arm, &note);

        transcript.step("run");
        read_until(arm, "the line run", |arm| {
            (!ran(arm, ECHO).is_empty()).then_some(())
        });
        arm.wait(Duration::from_millis(600));
        let lines = ran(arm, ECHO);
        transcript.record("linesRun", &lines);
        assert_eq!(lines, ["Some(\"new\")"], "{}", arm.name());
    });
}

const RELAY_VERB: &str = "parityrelay";

// OpenAC `PeerRelayParityTests.EitherClientHandsARelayWhatItTellsItsNeighbours`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_hands_a_relay_what_it_tells_its_neighbours() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        transcript.step("self");
        let me = arm.call::<calls::PeersCaptureSelf>(&());
        transcript.record("captured", me.is_some());
        let me = me.unwrap_or_else(|| panic!("{} would not say what it tells", arm.name()));
        transcript.record_id("playerId", me.player_id);
        transcript.record("name", &me.name);
        transcript.record("isRemote", me.is_remote);
        transcript.record("maxHealth", me.max_health);
        assert_eq!(me.player_id, world::PLAYER);
        assert_eq!(me.name, world::PLAYER_NAME);
        assert!(!me.is_remote);
        assert_ne!(me.client_id, 0);

        // A neighbour reads the same client under the same id.
        let mut onlooker = Neighbour::new(arm, 0xc9);
        let seen = the_arm(&mut onlooker, arm, |_| true);
        assert_eq!(
            (seen.client_id, seen.player_id),
            (me.client_id, me.player_id)
        );

        transcript.step("announced");
        for landed in [false, true] {
            assert!(arm.call::<calls::PeersAnnounceCast>(&CastAnnouncement {
                target_id: world::MONSTER,
                spell_id: SHARED_SPELL,
                effective_skill: 357,
                duration_seconds: if landed { 60.0 } else { 0.0 },
                landed,
            }));
        }
        assert!(arm.call::<calls::PeersBroadcast>(&Broadcast {
            line: format!("/{RELAY_VERB} go"),
            tags: vec!["squad".to_owned()],
            delay_ms: 250,
        }));
        let casts = arm.call::<calls::PeersOwnCasts>(&0);
        transcript.record("casts", casts.len());
        assert_eq!(casts.len(), 2, "{}: {casts:?}", arm.name());
        transcript.record("firstLanded", casts[0].landed);
        transcript.record("secondLanded", casts[1].landed);
        transcript.record_id("caster", casts[1].caster_id);
        assert!(!casts[0].landed && casts[1].landed);
        assert!(casts.iter().all(|c| c.caster_id == me.player_id));
        assert!(casts.iter().all(|c| c.client_id == me.client_id));
        assert!((55.0..=60.0).contains(&casts[1].seconds_remaining));
        assert!(
            arm.call::<calls::PeersOwnCasts>(&casts[1].sequence)
                .is_empty()
        );

        let commands = arm.call::<calls::PeersOwnCommands>(&0);
        assert_eq!(commands.len(), 1, "{}", arm.name());
        transcript.record("line", &commands[0].line);
        transcript.record("tags", &commands[0].tags);
        assert_eq!(commands[0].line, format!("/{RELAY_VERB} go"));
        assert_eq!(commands[0].tags, ["squad"]);
        assert_eq!(commands[0].sender_id, me.player_id);
    });
}

// OpenAC `PeerRelayParityTests.EitherClientTakesInARemotePeerItsCastsAndItsLines`.
#[test]
#[ignore = "needs the retail dats: a body whose place the record carries"]
fn either_client_takes_in_a_remote_peer_its_casts_and_its_lines() {
    run_with(&setup(), &[], true, |arm, transcript| {
        world::stage(arm);
        start_hub(arm);
        transcript.step("import");
        let remote = NetworkClient {
            name: "Faraway".to_owned(),
            ..neighbour_note(arm, REMOTE_CHARACTER, 55)
        };
        let own = arm.call::<calls::PeersImportClient>(&NetworkClient {
            player_id: world::PLAYER,
            ..remote.clone()
        });
        transcript.record("ownRefused", own);
        assert!(!own, "{}", arm.name());
        let accepted = arm.call::<calls::PeersImportClient>(&remote);
        transcript.record("accepted", accepted);
        let listed: Vec<NetworkClient> = arm
            .call::<calls::PeersClients>(&())
            .into_iter()
            .filter(|c| c.player_id == REMOTE_CHARACTER)
            .collect();
        assert_eq!(listed.len(), 1, "{} did not list the import", arm.name());
        transcript.record("isRemote", listed[0].is_remote);
        transcript.record("health", listed[0].current_health);
        assert!(listed[0].is_remote);
        assert_eq!(listed[0].current_health, 55);

        transcript.step("cast");
        let cast = |spell_id| RemoteCast {
            caster_id: REMOTE_CHARACTER,
            target_id: world::MONSTER,
            spell_id,
            effective_skill: 300,
            seconds_remaining: 30.0,
            landed: true,
        };
        let unknown = arm.call::<calls::PeersImportCast>(&cast(777));
        transcript.record("unknownSpellRefused", unknown);
        assert!(!unknown);
        assert!(arm.call::<calls::PeersImportCast>(&cast(SHARED_SPELL)));
        let casts = arm.call::<calls::PeersCasts>(&0);
        assert_eq!(casts.len(), 1, "{}: {casts:?}", arm.name());
        transcript.record("castIsRemote", casts[0].is_remote);
        transcript.record_id("caster", casts[0].caster_id);
        assert!(casts[0].is_remote);
        assert_eq!(casts[0].caster_id, REMOTE_CHARACTER);
        assert_eq!(casts[0].client_id, listed[0].client_id);
        assert!((25.0..=30.0).contains(&casts[0].seconds_remaining));

        transcript.step("line");
        assert!(arm.call::<calls::PeersImportCommand>(&RemoteBroadcast {
            sender_id: REMOTE_CHARACTER,
            line: format!("/{RELAY_VERB} go"),
            tags: Vec::new(),
            delay_ms: 0,
        }));
        read_until(arm, "the imported line run", |arm| {
            (!ran(arm, RELAY).is_empty()).then_some(())
        });
        let lines = ran(arm, RELAY);
        transcript.record("linesRun", &lines);
        assert_eq!(lines, ["Some(\"go\")"], "{}", arm.name());
        let read = arm.call::<calls::PeersCommands>(&0);
        assert_eq!(read.len(), 1, "{}", arm.name());
        transcript.record("lineIsRemote", read[0].is_remote);
        assert!(read[0].is_remote);
    });
}
