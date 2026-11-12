//! The record of portal space that did not end.
//!
//! A session that stays in portal space longer than the runtime allows ends
//! its connection and first tells what it knew
//! (`ac_runtime::session::PortalFailure`). Each one is appended, as one
//! line of JSON, to `portal-failures.jsonl` in the log folder: one file for
//! every client on the computer, outside the per-session folders, so the
//! failures of many clients can be read and counted in one place. The line
//! says what was waiting and what was missing; it holds message types and
//! counts, never what a message said.

use std::fmt::Write as _;
use std::io::Write as _;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

use ac_host::json::write_string;
use ac_runtime::session::{InboundNote, PortalFailure, PortalWait};

/// The file's name in the log folder.
pub(crate) const FILE_NAME: &str = "portal-failures.jsonl";

/// The file an over-long one is kept as (the one before it is dropped).
pub(crate) const OLDER_FILE_NAME: &str = "portal-failures.1.jsonl";

/// A file at least this long is set aside before the next line.
const ROTATE_AT: u64 = 5 * 1024 * 1024;

/// Where one session's portal failures are recorded, and who it is.
#[derive(Debug, Clone)]
pub(crate) struct PortalFailureLog {
    directory: PathBuf,
    server: String,
    account: String,
}

impl PortalFailureLog {
    /// Records in `logs_directory` for the session of `account` on `server`
    /// (as the session was told it: host and port).
    pub(crate) fn new(logs_directory: PathBuf, server: String, account: &str) -> Self {
        Self {
            directory: logs_directory,
            server,
            account: account.to_owned(),
        }
    }

    /// Append `failure` for `session_id` (in the world as `character`, when
    /// it got that far, with `plugins` loaded): the file it went to.
    pub(crate) fn record(
        &self,
        session_id: &str,
        character: Option<&str>,
        plugins: &[String],
        failure: &PortalFailure,
    ) -> std::io::Result<PathBuf> {
        let identity = Identity {
            session_id,
            server: &self.server,
            account: &self.account,
            character,
            plugins,
        };
        let line = record_line(failure, &identity, SystemTime::now());
        append(&self.directory, &line, ROTATE_AT)
    }
}

/// Who a record is about.
#[derive(Debug, Clone, Copy)]
struct Identity<'a> {
    session_id: &'a str,
    server: &'a str,
    account: &'a str,
    character: Option<&'a str>,
    plugins: &'a [String],
}

/// Append `line` to the file in `directory`, setting the file aside first
/// when it has grown to `rotate_at` bytes.
fn append(directory: &Path, line: &str, rotate_at: u64) -> std::io::Result<PathBuf> {
    std::fs::create_dir_all(directory)?;
    let file = directory.join(FILE_NAME);
    if std::fs::metadata(&file).is_ok_and(|m| m.len() >= rotate_at) {
        // Another client may have set it aside in the same moment; the
        // line is appended to whichever file is there then.
        let _ = std::fs::rename(&file, directory.join(OLDER_FILE_NAME));
    }
    let mut out = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&file)?;
    // One write, so lines of clients writing at once do not interleave.
    out.write_all(format!("{line}\n").as_bytes())?;
    Ok(file)
}

fn millis(duration: Duration) -> u128 {
    duration.as_millis()
}

fn hex(value: u32) -> String {
    format!("\"0x{value:08X}\"")
}

fn opcode(value: u32) -> String {
    format!("\"0x{value:04X}\"")
}

fn quoted(text: &str) -> String {
    let mut out = String::new();
    write_string(&mut out, text);
    out
}

fn wait_name(wait: PortalWait) -> &'static str {
    match wait {
        PortalWait::Login => "login",
        PortalWait::Teleport => "teleport",
    }
}

fn note_json(note: &InboundNote) -> String {
    let event = note
        .game_event
        .map_or_else(String::new, |event| format!(",\"ev\":{}", opcode(event)));
    format!(
        "{{\"ms\":{},\"op\":{}{event},\"bytes\":{},\"ok\":{}}}",
        millis(note.at),
        opcode(note.opcode),
        note.bytes,
        note.parsed
    )
}

/// One record as a line of JSON (no line break).
fn record_line(failure: &PortalFailure, identity: &Identity<'_>, at: SystemTime) -> String {
    let world = &failure.world;
    let reveal = &world.reveal;
    let link = &failure.link;
    let stats = &link.stats;
    let mut out = String::from("{\"v\":1");
    let _ = write!(
        out,
        ",\"t\":{},\"clientVersion\":{},\"sessionId\":{},\"server\":{},\"account\":{}",
        // Digits and punctuation only: written as it reads, where the
        // string writer would escape its plus sign.
        format_args!("\"{}\"", ac_host::status::Timestamp(at)),
        quoted(env!("CARGO_PKG_VERSION")),
        quoted(identity.session_id),
        quoted(identity.server),
        quoted(identity.account),
    );
    let _ = write!(
        out,
        ",\"character\":{}",
        identity.character.map_or_else(|| "null".to_owned(), quoted)
    );
    let plugins: Vec<String> = identity.plugins.iter().map(|p| quoted(p)).collect();
    let _ = write!(out, ",\"plugins\":[{}]", plugins.join(","));
    let _ = write!(
        out,
        ",\"wait\":{},\"waitedMs\":{},\"limitMs\":{},\"sessionTimeMs\":{}",
        quoted(wait_name(failure.wait)),
        millis(failure.waited),
        millis(failure.limit),
        millis(failure.at),
    );
    let _ = write!(
        out,
        ",\"phase\":{},\"loginCompleteSent\":{},\"loginCompleteHeld\":{}",
        quoted(&format!("{:?}", failure.phase)),
        failure.login_complete_sent,
        failure.login_complete_held,
    );
    let _ = write!(
        out,
        ",\"teleportSequence\":{},\"destinationCell\":{}",
        world.teleport_sequence,
        hex(reveal.destination_cell()),
    );
    let readiness = &reveal.readiness;
    let _ = write!(
        out,
        ",\"reveal\":{{\"generation\":{},\"kind\":{},\"materialized\":{},\"completed\":{},\
         \"cancelled\":{},\"waitCueShown\":{},\"invariantFailures\":{},\
         \"staleAcknowledgements\":{},\"indoor\":{},\"unhydratable\":{},\"renderReady\":{},\
         \"texturesReady\":{},\"collisionReady\":{}}}",
        reveal.generation,
        quoted(&format!("{:?}", reveal.kind)),
        reveal.materialized,
        reveal.completed,
        reveal.cancelled,
        reveal.wait_cue_shown,
        reveal.invariant_failure_count,
        world.stale_acknowledgements,
        readiness.is_indoor,
        readiness.is_unhydratable,
        readiness.is_render_neighborhood_ready,
        readiness.are_composite_textures_ready,
        readiness.is_collision_ready,
    );
    let _ = write!(
        out,
        ",\"playerGuid\":{},\"playerObject\":{},\"playerDescribed\":{},\"objects\":{}",
        world.player_guid.map_or_else(|| "null".to_owned(), hex),
        world.player_object,
        world.player_described,
        world.objects,
    );
    let failures: Vec<String> = world
        .parse_failures
        .iter()
        .map(|(code, count)| format!("\"0x{code:04X}\":{count}"))
        .collect();
    let _ = write!(out, ",\"parseFailures\":{{{}}}", failures.join(","));
    let _ = write!(
        out,
        ",\"windowAttached\":{},\"windowMinimized\":{},\"heldForWindow\":{},\
         \"heldForCollision\":{},\"collisionReady\":{},\"simulatesWorld\":{},\"playerPlaced\":{}",
        world.window_attached,
        world.window_minimized,
        world.held_for_window,
        world.held_for_collision,
        world
            .collision_ready
            .map_or_else(|| "null".to_owned(), |ready| ready.to_string()),
        world.simulates_world,
        world.player_placed,
    );
    let loss = if link.packet_loss_percent.is_finite() {
        format!("{:.2}", link.packet_loss_percent)
    } else {
        "null".to_owned()
    };
    let _ = write!(
        out,
        ",\"link\":{{\"sinceLastPacketMs\":{},\"missingPackets\":{},\"partialMessages\":{},\
         \"unhandledInbound\":{},\"packetsReceived\":{},\"packetsSent\":{},\"resendRequestsSent\":{},\
         \"resendIdsRequested\":{},\"resendsRefused\":{},\"resendsSent\":{},\
         \"checksumFailures\":{},\"sanityDrops\":{},\"duplicatesDropped\":{},\
         \"highestReceived\":{},\"highestSent\":{},\"lossPercent\":{loss}}}",
        millis(failure.since_last_packet),
        link.nak_set,
        link.partial_messages,
        failure.unhandled_inbound,
        stats.packets_received,
        stats.packets_sent,
        stats.naks_sent,
        stats.nak_ids_sent,
        stats.rejects_received,
        stats.resends_sent,
        stats.checksum_failures,
        stats.inbound_sanity_drops,
        stats.inbound_dups_dropped,
        link.highest_id_received,
        link.highest_id_sent,
    );
    let recent: Vec<String> = failure.recent.iter().map(note_json).collect();
    let _ = write!(out, ",\"recent\":[{}]}}", recent.join(","));
    out
}

/// What a person reads in the client's log: which wait, how long, and what
/// was missing.
pub(crate) fn summary(failure: &PortalFailure) -> String {
    let world = &failure.world;
    let mut missing = Vec::new();
    if !world.player_object {
        missing.push("the character's object");
    }
    if !world.player_described {
        missing.push("the character's description");
    }
    if world.held_for_window {
        missing.push("the window's word that it showed the destination");
    }
    if world.held_for_collision {
        missing.push("the destination's collision");
    }
    if world.simulates_world && !world.player_placed {
        missing.push("the character's place in the world");
    }
    let missing = if missing.is_empty() {
        "nothing known to be missing".to_owned()
    } else {
        format!("missing {}", missing.join(", "))
    };
    let last = failure.recent.last().map_or_else(
        || "no message was received".to_owned(),
        |note| {
            format!(
                "last message 0x{:04X} {:.1} s before",
                note.opcode,
                failure.at.saturating_sub(note.at).as_secs_f64()
            )
        },
    );
    format!(
        "portal failure: the {} did not end in {:.1} s (limit {} s) while {:?}, to \
         0x{:08X}; {missing}; login complete {}; {} parse failures; link: last packet {:.1} s \
         ago, {} packets missing, {} resends refused, {} messages incomplete; {last}; the \
         connection ends",
        wait_name(failure.wait),
        failure.waited.as_secs_f64(),
        failure.limit.as_secs(),
        failure.phase,
        world.reveal.destination_cell(),
        if failure.login_complete_sent {
            "sent"
        } else if failure.login_complete_held {
            "held"
        } else {
            "not sent"
        },
        world.parse_failures.values().sum::<u64>(),
        failure.since_last_packet.as_secs_f64(),
        failure.link.nak_set,
        failure.link.stats.rejects_received,
        failure.link.partial_messages,
    )
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;
    use std::time::UNIX_EPOCH;

    use ac_net::{LinkSnapshot, TransportStats};
    use ac_runtime::session::{Phase, PortalWorldState};
    use ac_runtime::world::transit::PortalSnapshot;

    use super::*;

    /// A login that waited 61.5 s for a character that never came, having
    /// last heard the server say it was ready.
    fn failure() -> PortalFailure {
        PortalFailure {
            wait: PortalWait::Login,
            waited: Duration::from_millis(61_500),
            limit: Duration::from_secs(60),
            at: Duration::from_millis(63_000),
            phase: Phase::InWorld,
            login_complete_sent: false,
            login_complete_held: false,
            world: PortalWorldState {
                teleport_sequence: 0,
                reveal: PortalSnapshot::IDLE,
                stale_acknowledgements: 0,
                player_guid: Some(0x5000_000C),
                player_object: false,
                player_described: false,
                objects: 0,
                parse_failures: BTreeMap::from([(0xF745, 1)]),
                window_attached: true,
                window_minimized: false,
                held_for_window: false,
                held_for_collision: false,
                collision_ready: None,
                simulates_world: false,
                player_placed: false,
            },
            link: LinkSnapshot {
                stats: TransportStats {
                    packets_received: 12,
                    rejects_received: 2,
                    ..TransportStats::default()
                },
                nak_set: 3,
                reclaimed_words: 0,
                cache_depth: 0,
                pending_resends: 0,
                highest_id_received: 14,
                highest_id_sent: 9,
                ack_watermark: 9,
                partial_messages: 1,
                packet_loss_percent: 12.5,
                handshake_confirmed: true,
            },
            since_last_packet: Duration::from_millis(400),
            unhandled_inbound: 0,
            recent: vec![
                InboundNote {
                    at: Duration::from_millis(900),
                    opcode: 0xF7B0,
                    game_event: Some(0x0013),
                    bytes: 4096,
                    parsed: true,
                },
                InboundNote {
                    at: Duration::from_millis(1_500),
                    opcode: 0xF7DF,
                    game_event: None,
                    bytes: 4,
                    parsed: true,
                },
            ],
        }
    }

    const IDENTITY: Identity<'static> = Identity {
        session_id: "s1",
        server: "play.example:9000",
        account: "a \"quoted\" account",
        character: None,
        plugins: &[],
    };

    #[test]
    fn a_record_is_one_line_of_json_that_says_what_was_missing() {
        let plugins = ["one.plugin".to_owned()];
        let identity = Identity {
            character: Some("Somebody"),
            plugins: &plugins,
            ..IDENTITY
        };
        let line = record_line(&failure(), &identity, UNIX_EPOCH);
        assert!(!line.contains('\n'));
        // It reads as JSON (the client's own strict reader).
        ac_host::json::parse(line.as_bytes()).expect("valid JSON");
        for part in [
            "{\"v\":1,\"t\":\"1970-01-01T00:00:00.0000000+00:00\"",
            "\"account\":\"a \\\"quoted\\\" account\"",
            "\"character\":\"Somebody\"",
            "\"plugins\":[\"one.plugin\"]",
            "\"wait\":\"login\",\"waitedMs\":61500,\"limitMs\":60000,\"sessionTimeMs\":63000",
            "\"phase\":\"InWorld\",\"loginCompleteSent\":false",
            "\"playerGuid\":\"0x5000000C\",\"playerObject\":false,\"playerDescribed\":false",
            "\"parseFailures\":{\"0xF745\":1}",
            "\"windowAttached\":true",
            "\"collisionReady\":null",
            "\"sinceLastPacketMs\":400,\"missingPackets\":3,\"partialMessages\":1",
            "\"resendsRefused\":2",
            "\"lossPercent\":12.50",
            "\"recent\":[{\"ms\":900,\"op\":\"0xF7B0\",\"ev\":\"0x0013\",\"bytes\":4096,\"ok\":true},\
             {\"ms\":1500,\"op\":\"0xF7DF\",\"bytes\":4,\"ok\":true}]}",
        ] {
            assert!(line.contains(part), "{part} is not in {line}");
        }
        // Without a character the field is null.
        let line = record_line(&failure(), &IDENTITY, UNIX_EPOCH);
        assert!(line.contains("\"character\":null,\"plugins\":[]"), "{line}");
    }

    #[test]
    fn the_log_line_names_the_wait_and_what_was_missing() {
        assert_eq!(
            summary(&failure()),
            "portal failure: the login did not end in 61.5 s (limit 60 s) while InWorld, to \
             0x00000000; missing the character's object, the character's description; login \
             complete not sent; 1 parse failures; link: last packet 0.4 s ago, 3 packets \
             missing, 2 resends refused, 1 messages incomplete; last message 0xF7DF 61.5 s \
             before; the connection ends"
        );
    }

    fn fresh_directory(name: &str) -> PathBuf {
        let directory = std::env::temp_dir().join(format!(
            "openac-portal-failure-{name}-{}-{:x}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&directory);
        directory
    }

    #[test]
    fn records_are_appended_and_a_long_file_is_set_aside() {
        let directory = fresh_directory("append").join("logs");
        let file = append(&directory, "first", 12).unwrap();
        assert_eq!(file, directory.join(FILE_NAME));
        append(&directory, "second", 12).unwrap();
        assert_eq!(std::fs::read_to_string(&file).unwrap(), "first\nsecond\n");
        // 13 bytes is over the 12 allowed: the next line starts a new file.
        append(&directory, "third", 12).unwrap();
        assert_eq!(std::fs::read_to_string(&file).unwrap(), "third\n");
        assert_eq!(
            std::fs::read_to_string(directory.join(OLDER_FILE_NAME)).unwrap(),
            "first\nsecond\n"
        );
        let _ = std::fs::remove_dir_all(directory.parent().unwrap());
    }

    #[test]
    fn a_session_records_into_the_shared_file_of_its_log_folder() {
        let directory = fresh_directory("record");
        let log = PortalFailureLog::new(directory.clone(), "host:9000".into(), "account");
        let file = log.record("s1", Some("Somebody"), &[], &failure()).unwrap();
        log.record("s2", None, &[], &failure()).unwrap();
        let text = std::fs::read_to_string(&file).unwrap();
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), 2);
        assert!(lines[0].contains("\"sessionId\":\"s1\",\"server\":\"host:9000\""));
        assert!(lines[1].contains("\"sessionId\":\"s2\""));
        let _ = std::fs::remove_dir_all(&directory);
    }
}
