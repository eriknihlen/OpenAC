//! Status lines, the status-file tailer and server health. Ported from
//! OpenAC `tests/AcDream.Launcher.Core.Tests/Status/` (StatusEventParserTests,
//! StatusFileTailerTests, ServerHealthServiceTests). The UDP probe itself is
//! the host's (no sockets in this crate); its request and reply rules are
//! tested here.

mod common;

use std::io::Write;
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::status::{
    ReachabilityProbe, ServerHealthService, StatusEvent, StatusEventKind, StatusEventSource,
    StatusFileTailer, find_player_count, is_status_reply, parse_player_counts, parse_status_line,
};
use ac_launcher_core::time::parse_iso8601;
use common::TempDir;
use common::fixtures::{FakeServer, Route};

fn is_malformed(event: &StatusEvent) -> bool {
    matches!(event.kind, StatusEventKind::Malformed { .. })
}

fn is_unknown(event: &StatusEvent) -> bool {
    matches!(event.kind, StatusEventKind::Unknown { .. })
}

// --- StatusEventParserTests ---------------------------------------------------

#[test]
fn parses_started() {
    let event =
        parse_status_line(r#"{"v":1,"e":"started","t":"2026-08-14T12:00:00Z","sessionId":"s1"}"#);
    assert_eq!(event.kind, StatusEventKind::Started);
    assert_eq!(event.envelope.v, 1);
    assert_eq!(event.envelope.e, "started");
    assert_eq!(event.envelope.session_id, "s1");
    assert_eq!(
        event.envelope.t,
        Some(parse_iso8601("2026-08-14T12:00:00Z").unwrap().instant)
    );
}

#[test]
fn parses_connected() {
    let event =
        parse_status_line(r#"{"v":1,"e":"connected","t":"2026-08-14T12:00:01Z","sessionId":"s1"}"#);
    assert_eq!(event.kind, StatusEventKind::Connected);
}

#[test]
fn parses_character_list_with_multiple_characters() {
    let event = parse_status_line(
        r#"
        {"v":1,"e":"characterList","t":"2026-08-14T12:00:02Z","sessionId":"s1",
         "accountName":"testaccount","slotCount":6,
         "characters":[
           {"id":1342177290,"name":"+Acdream","secondsGreyedOut":0},
           {"id":1342177291,"name":"+Second","secondsGreyedOut":1}
         ]}
        "#,
    );
    let StatusEventKind::CharacterList {
        account_name,
        slot_count,
        characters,
    } = event.kind
    else {
        panic!("expected a character list: {event:?}");
    };
    assert_eq!(account_name, "testaccount");
    assert_eq!(slot_count, 6);
    assert_eq!(characters.len(), 2);
    assert_eq!(characters[0].id, 1_342_177_290);
    assert_eq!(characters[0].name, "+Acdream");
    assert_eq!(characters[0].seconds_greyed_out, 0);
    assert_eq!(characters[1].id, 1_342_177_291);
    assert_eq!(characters[1].seconds_greyed_out, 1);
}

#[test]
fn parses_entered_world() {
    let event = parse_status_line(
        r#"{"v":1,"e":"enteredWorld","t":"2026-08-14T12:00:03Z","sessionId":"s1","characterId":1342177290,"characterName":"+Acdream"}"#,
    );
    assert_eq!(
        event.kind,
        StatusEventKind::EnteredWorld {
            character_id: 1_342_177_290,
            character_name: "+Acdream".into()
        }
    );
}

#[test]
fn parses_plugin_loaded_and_plugin_failed() {
    let loaded = parse_status_line(
        r#"{"v":1,"e":"pluginLoaded","t":"2026-08-14T12:00:04Z","sessionId":"s1","plugin":"ExamplePlugin"}"#,
    );
    assert_eq!(
        loaded.kind,
        StatusEventKind::PluginLoaded {
            plugin: "ExamplePlugin".into()
        }
    );
    let failed = parse_status_line(
        r#"{"v":1,"e":"pluginFailed","t":"2026-08-14T12:00:05Z","sessionId":"s1","plugin":"BadPlugin","error":"boom"}"#,
    );
    assert_eq!(
        failed.kind,
        StatusEventKind::PluginFailed {
            plugin: "BadPlugin".into(),
            error: "boom".into()
        }
    );
}

#[test]
fn parses_character_created_and_creation_failed() {
    let created = parse_status_line(
        r#"{"v":1,"e":"characterCreated","t":"2026-08-15T12:00:00Z","sessionId":"s1","guid":1342177296,"name":"NewChar"}"#,
    );
    assert_eq!(
        created.kind,
        StatusEventKind::CharacterCreated {
            guid: 1_342_177_296,
            name: "NewChar".into()
        }
    );
    let failed = parse_status_line(
        r#"{"v":1,"e":"creationFailed","t":"2026-08-15T12:00:01Z","sessionId":"s1","code":3,"reason":"NameInUse","name":"Bob"}"#,
    );
    assert_eq!(
        failed.kind,
        StatusEventKind::CreationFailed {
            code: 3,
            reason: "NameInUse".into(),
            name: "Bob".into()
        }
    );
}

#[test]
fn malformed_character_creation_events_use_the_known_event_failure_path() {
    for line in [
        r#"{"v":1,"e":"characterCreated","t":"2026-08-15T12:00:00Z","sessionId":"s1","name":"NewChar"}"#,
        r#"{"v":1,"e":"characterCreated","t":"2026-08-15T12:00:00Z","sessionId":"s1","guid":1342177296}"#,
        r#"{"v":1,"e":"creationFailed","t":"2026-08-15T12:00:00Z","sessionId":"s1","reason":"NameInUse","name":"Bob"}"#,
        r#"{"v":1,"e":"creationFailed","t":"2026-08-15T12:00:00Z","sessionId":"s1","code":3,"name":"Bob"}"#,
        r#"{"v":1,"e":"creationFailed","t":"2026-08-15T12:00:00Z","sessionId":"s1","code":3,"reason":"NameInUse"}"#,
    ] {
        let event = parse_status_line(line);
        let StatusEventKind::Malformed { error } = &event.kind else {
            panic!("{line}");
        };
        assert_eq!(event.envelope.session_id, "s1");
        assert!(!error.trim().is_empty());
    }
}

#[test]
fn parses_login_command_failed() {
    let event = parse_status_line(
        r#"{"v":1,"e":"loginCommandFailed","t":"2026-08-14T12:00:05Z","sessionId":"s1","commandIndex":2,"command":"/version","error":"unsupported headless command"}"#,
    );
    assert_eq!(
        event.kind,
        StatusEventKind::LoginCommandFailed {
            command_index: 2,
            command: "/version".into(),
            error: "unsupported headless command".into()
        }
    );
}

#[test]
fn malformed_login_command_failure_uses_the_known_event_failure_path() {
    for line in [
        r#"{"v":1,"e":"loginCommandFailed","t":"2026-08-14T12:00:05Z","sessionId":"s1","commandIndex":-1,"command":"/version","error":"unsupported"}"#,
        r#"{"v":1,"e":"loginCommandFailed","t":"2026-08-14T12:00:05Z","sessionId":"s1","commandIndex":0,"error":"unsupported"}"#,
        r#"{"v":1,"e":"loginCommandFailed","t":"2026-08-14T12:00:05Z","sessionId":"s1","commandIndex":0,"command":"/version"}"#,
    ] {
        let event = parse_status_line(line);
        assert!(is_malformed(&event), "{line}");
        assert_eq!(event.envelope.e, "loginCommandFailed");
        assert_eq!(event.envelope.session_id, "s1");
    }
}

#[test]
fn parses_disconnected_and_exited() {
    let disconnected = parse_status_line(
        r#"{"v":1,"e":"disconnected","t":"2026-08-14T12:00:06Z","sessionId":"s1","reason":"serverClosed"}"#,
    );
    assert_eq!(
        disconnected.kind,
        StatusEventKind::Disconnected {
            reason: "serverClosed".into()
        }
    );
    let exited = parse_status_line(
        r#"{"v":1,"e":"exited","t":"2026-08-14T12:00:07Z","sessionId":"s1","code":0,"reason":"graceful"}"#,
    );
    assert_eq!(
        exited.kind,
        StatusEventKind::Exited {
            code: 0,
            reason: "graceful".into(),
            server_message: None,
            account_in_use: false,
        }
    );
}

// Not an OpenAC test: the client's additions, the server's refusal on
// `exited` and the heartbeat.
#[test]
fn exited_carries_the_servers_refusal_and_heartbeat_is_known() {
    let exited = parse_status_line(
        r#"{"v":1,"e":"exited","t":"2026-08-14T12:00:07Z","sessionId":"s1","code":6,"reason":"the server ended the session","serverMessage":"Another account is already logged on from this client.","accountInUse":true}"#,
    );
    assert_eq!(
        exited.kind,
        StatusEventKind::Exited {
            code: 6,
            reason: "the server ended the session".into(),
            server_message: Some("Another account is already logged on from this client.".into()),
            account_in_use: true,
        }
    );
    let heartbeat =
        parse_status_line(r#"{"v":1,"e":"heartbeat","t":"2026-08-14T12:00:08Z","sessionId":"s1"}"#);
    assert_eq!(heartbeat.kind, StatusEventKind::Heartbeat);
}

#[test]
fn unknown_e_value_surfaces_as_unknown_event_rather_than_throwing() {
    let event = parse_status_line(
        r#"{"v":1,"e":"someFutureEvent","t":"2026-08-14T12:00:08Z","sessionId":"s1","extra":true}"#,
    );
    let StatusEventKind::Unknown { raw_json } = &event.kind else {
        panic!("{event:?}");
    };
    assert_eq!(event.envelope.e, "someFutureEvent");
    assert_eq!(event.envelope.session_id, "s1");
    assert!(raw_json.contains("someFutureEvent"));
}

#[test]
fn malformed_json_surfaces_as_unknown_event_rather_than_throwing() {
    assert!(is_unknown(&parse_status_line("{not json")));
}

#[test]
fn complete_json_with_a_non_object_root_surfaces_as_malformed_event() {
    for line in ["[]", "null", "42", "\"text\""] {
        let event = parse_status_line(line);
        let StatusEventKind::Malformed { error } = &event.kind else {
            panic!("{line}");
        };
        assert!(error.to_lowercase().contains("root"));
    }
}

#[test]
fn whitespace_or_empty_line_surfaces_as_unknown_event_rather_than_throwing() {
    for line in ["", "   ", "\t"] {
        assert!(is_unknown(&parse_status_line(line)), "{line:?}");
    }
}

#[test]
fn payload_free_known_events_require_the_full_pinned_v1_envelope() {
    for line in [
        r#"{"e":"started","t":"2026-08-14T12:00:00Z","sessionId":"s1"}"#,
        r#"{"v":"1","e":"connected","t":"2026-08-14T12:00:00Z","sessionId":"s1"}"#,
        r#"{"v":2,"e":"started","t":"2026-08-14T12:00:00Z","sessionId":"s1"}"#,
        r#"{"v":1,"e":"connected","sessionId":"s1"}"#,
        r#"{"v":1,"e":"started","t":42,"sessionId":"s1"}"#,
        r#"{"v":1,"e":"connected","t":"not-a-time","sessionId":"s1"}"#,
        r#"{"v":1,"e":"started","t":"2026-08-14T12:00:00+02:00","sessionId":"s1"}"#,
        r#"{"v":1,"e":"connected","t":"2026-08-14T12:00:00Z"}"#,
        r#"{"v":1,"e":"started","t":"2026-08-14T12:00:00Z","sessionId":42}"#,
        r#"{"v":1,"e":"connected","t":"2026-08-14T12:00:00Z","sessionId":""}"#,
    ] {
        assert!(is_malformed(&parse_status_line(line)), "{line}");
    }
}

#[test]
fn missing_or_wrong_kind_event_name_surfaces_as_malformed_event() {
    for line in [
        r#"{"v":1,"t":"2026-08-14T12:00:00Z","sessionId":"s1"}"#,
        r#"{"v":1,"e":42,"t":"2026-08-14T12:00:00Z","sessionId":"s1"}"#,
    ] {
        assert!(is_malformed(&parse_status_line(line)), "{line}");
    }
}

#[test]
fn known_e_value_with_missing_required_field_surfaces_as_malformed_event() {
    let event = parse_status_line(
        r#"{"v":1,"e":"characterList","t":"2026-08-14T12:00:09Z","sessionId":"s1","accountName":"a","slotCount":6}"#,
    );
    assert!(is_malformed(&event));
    assert_eq!(event.envelope.e, "characterList");
    assert_eq!(event.envelope.session_id, "s1");
}

#[test]
fn known_e_value_with_a_field_of_the_wrong_json_kind_surfaces_as_malformed_event() {
    let event = parse_status_line(
        r#"{"v":1,"e":"characterList","t":"2026-08-14T12:00:10Z","sessionId":"s1","accountName":"a","slotCount":6,"characters":"not-an-array"}"#,
    );
    assert!(is_malformed(&event));
    assert_eq!(event.envelope.e, "characterList");
}

#[test]
fn every_line_the_host_writer_produces_parses_back() {
    // Cross-check against this project's own writer (ac-host StatusWriter).
    let root = TempDir::new("status-writer");
    let path = root.join("status.jsonl");
    let mut writer = ac_host::StatusWriter::for_file(Some(&path));
    writer.started("s1");
    writer.connected("s1");
    writer.character_list(
        "s1",
        &ac_host::RosterReport {
            account_name: "testaccount".into(),
            slot_count: 6,
            entries: vec![ac_host::RosterEntry {
                id: 0x5000_000A,
                name: "+Acdream".into(),
                seconds_greyed_out: 0,
            }],
        },
    );
    writer.entered_world("s1", 0x5000_000A, "+Acdream");
    writer.plugin_loaded("s1", "Example.Plugin");
    writer.login_command_failed("s1", 0, "/version", "unsupported");
    writer.window_opened("s1");
    writer.window_closed("s1", "closedByUser");
    writer.window_failed("s1", "no window support");
    writer.heartbeat("s1");
    writer.exited("s1", 0, "graceful");
    writer.close();
    let events = StatusFileTailer::new(&path).read_new_events();
    assert!(events.len() >= 10, "{events:?}");
    for event in &events {
        assert!(!is_malformed(event) && !is_unknown(event), "{event:?}");
        assert_eq!(event.envelope.session_id, "s1");
    }
}

#[test]
fn parses_the_window_events() {
    // An addition over OpenAC: the client reports its game
    // window so a running row shows its real mode.
    let opened = parse_status_line(
        r#"{"v":1,"e":"windowOpened","t":"2026-10-16T12:00:00Z","sessionId":"s1"}"#,
    );
    assert_eq!(opened.kind, StatusEventKind::WindowOpened);
    let closed = parse_status_line(
        r#"{"v":1,"e":"windowClosed","t":"2026-10-16T12:00:01Z","sessionId":"s1","reason":"requested"}"#,
    );
    assert_eq!(
        closed.kind,
        StatusEventKind::WindowClosed {
            reason: "requested".into()
        }
    );
    let failed = parse_status_line(
        r#"{"v":1,"e":"windowFailed","t":"2026-10-16T12:00:02Z","sessionId":"s1","error":"no GPU"}"#,
    );
    assert_eq!(
        failed.kind,
        StatusEventKind::WindowFailed {
            error: "no GPU".into()
        }
    );
    assert!(is_malformed(&parse_status_line(
        r#"{"v":1,"e":"windowClosed","t":"2026-10-16T12:00:01Z","sessionId":"s1"}"#
    )));
}

// --- StatusFileTailerTests ----------------------------------------------------

fn line(e: &str, session: &str) -> String {
    format!(r#"{{"v":1,"e":"{e}","t":"2026-08-14T12:00:00Z","sessionId":"{session}"}}"#) + "\n"
}

fn append(path: &std::path::Path, text: &str) {
    let mut file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .unwrap();
    file.write_all(text.as_bytes()).unwrap();
}

#[test]
fn returns_no_events_when_the_file_does_not_exist_yet() {
    let root = TempDir::new("tailer");
    let mut tailer = StatusFileTailer::new(&root.join("status.jsonl"));
    assert!(tailer.read_new_events().is_empty());
}

#[test]
fn returns_no_events_when_nothing_has_been_appended_since_the_last_poll() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    append(&path, &line("started", "s1"));
    let mut tailer = StatusFileTailer::new(&path);
    assert_eq!(tailer.read_new_events().len(), 1);
    assert!(tailer.read_new_events().is_empty());
}

#[test]
fn reads_multiple_complete_lines_in_one_poll() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    append(&path, &(line("started", "s1") + &line("connected", "s1")));
    let events = StatusFileTailer::new(&path).read_new_events();
    assert_eq!(events.len(), 2);
    assert_eq!(events[0].kind, StatusEventKind::Started);
    assert_eq!(events[1].kind, StatusEventKind::Connected);
}

#[test]
fn continues_past_complete_non_object_json_values_to_the_following_valid_line() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    append(
        &path,
        &("[]\nnull\n42\n\"text\"\n".to_owned() + &line("connected", "s1")),
    );
    let events = StatusFileTailer::new(&path).read_new_events();
    assert_eq!(events.len(), 5);
    assert!(events[..4].iter().all(is_malformed));
    assert_eq!(events[4].kind, StatusEventKind::Connected);
}

#[test]
fn tolerates_a_partial_last_line_and_completes_it_on_a_later_poll() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    let full = line("started", "s1");
    let split = full.len() - 10;
    append(&path, &full[..split]);
    let mut tailer = StatusFileTailer::new(&path);
    assert!(tailer.read_new_events().is_empty());
    append(&path, &full[split..]);
    let events = tailer.read_new_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].kind, StatusEventKind::Started);
}

#[test]
fn a_partial_line_followed_by_a_full_line_only_emits_the_complete_one() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    append(&path, &line("started", "s1"));
    append(
        &path,
        r#"{"v":1,"e":"connected","t":"2026-08-14T12:00:00Z","sessionId":"s1"#,
    );
    let mut tailer = StatusFileTailer::new(&path);
    let events = tailer.read_new_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].kind, StatusEventKind::Started);
    append(&path, "\"}\n");
    let events = tailer.read_new_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].kind, StatusEventKind::Connected);
}

#[test]
fn skips_blank_lines() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    append(
        &path,
        &("\n".to_owned() + &line("started", "s1") + "\n" + &line("connected", "s1")),
    );
    assert_eq!(StatusFileTailer::new(&path).read_new_events().len(), 2);
}

#[test]
fn reads_with_a_writer_holding_the_file_open_for_append() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    let mut writer = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
        .unwrap();
    let mut tailer = StatusFileTailer::new(&path);
    writer.write_all(line("started", "s1").as_bytes()).unwrap();
    writer.flush().unwrap();
    assert_eq!(tailer.read_new_events().len(), 1);
    writer
        .write_all(line("connected", "s1").as_bytes())
        .unwrap();
    writer.flush().unwrap();
    let events = tailer.read_new_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].kind, StatusEventKind::Connected);
}

#[cfg(windows)]
#[test]
fn read_new_events_returns_empty_rather_than_throwing_on_a_sharing_violation() {
    use std::os::windows::fs::OpenOptionsExt;
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    append(&path, &line("started", "s1"));
    let _exclusive = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .share_mode(0)
        .open(&path)
        .unwrap();
    assert!(StatusFileTailer::new(&path).read_new_events().is_empty());
}

#[test]
fn tails_character_created_and_creation_failed_events() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    append(
        &path,
        concat!(
            r#"{"v":1,"e":"characterCreated","t":"2026-08-15T12:00:00Z","sessionId":"s1","guid":1342177296,"name":"NewChar"}"#,
            "\n",
            r#"{"v":1,"e":"creationFailed","t":"2026-08-15T12:00:01Z","sessionId":"s1","code":3,"reason":"NameInUse","name":"Bob"}"#,
            "\n"
        ),
    );
    let events = StatusFileTailer::new(&path).read_new_events();
    assert_eq!(events.len(), 2);
    assert!(matches!(
        events[0].kind,
        StatusEventKind::CharacterCreated {
            guid: 1_342_177_296,
            ..
        }
    ));
    assert!(matches!(
        events[1].kind,
        StatusEventKind::CreationFailed { code: 3, .. }
    ));
}

#[test]
fn restarts_from_the_top_when_the_file_is_truncated_or_replaced() {
    let root = TempDir::new("tailer");
    let path = root.join("status.jsonl");
    append(&path, &(line("started", "s1") + &line("connected", "s1")));
    let mut tailer = StatusFileTailer::new(&path);
    assert_eq!(tailer.read_new_events().len(), 2);
    std::fs::remove_file(&path).unwrap();
    append(&path, &line("started", "s2"));
    let events = tailer.read_new_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].session_id(), "s2");
}

// --- ServerHealthServiceTests -------------------------------------------------

#[test]
fn population_matches_hostname_when_display_name_is_an_address() {
    let counts = parse_player_counts(r#"[{"server":"Coldeve","count":807}]"#).unwrap();
    assert_eq!(
        find_player_count(&counts, "play.coldeve.ac", "play.coldeve.ac"),
        Some(807)
    );
    assert_eq!(
        find_player_count(&counts, "My favourite", "play.coldeve.ac"),
        Some(807)
    );
    assert_eq!(find_player_count(&counts, "Local", "localhost"), None);
    assert_eq!(
        find_player_count(&counts, "Another", "notcoldeve.example"),
        None
    );
}

struct FixedProbe(Option<f64>);

impl ReachabilityProbe for FixedProbe {
    fn probe(&self, _: &str, _: u16, _: Duration, _: &CancelToken) -> Option<f64> {
        self.0
    }
}

const POPULATION: &str = "https://treestats.net/player_counts-latest.json";

fn population_server() -> FakeServer {
    let server = FakeServer::new();
    server.add(
        POPULATION,
        br#"[{"server":"Empty","count":0},{"server":"Busy","count":27}]"#,
    );
    server
}

#[test]
fn shares_population_fetch_and_preserves_zero_and_missing_counts() {
    let server = population_server();
    let service = Arc::new(ServerHealthService::new(
        server.transport(),
        Arc::new(FixedProbe(Some(12.0))),
    ));
    let snapshots: Vec<_> = std::thread::scope(|scope| {
        ["Empty", "Unknown", "Busy"]
            .map(|name| {
                let service = Arc::clone(&service);
                scope.spawn(move || service.check("localhost", 9000, name, &CancelToken::none()))
            })
            .map(|handle| handle.join().unwrap().unwrap())
            .to_vec()
    });
    assert_eq!(server.count(POPULATION), 1);
    assert_eq!(snapshots[0].player_count, Some(0));
    assert_eq!(snapshots[1].player_count, None);
    assert_eq!(snapshots[2].player_count, Some(27));
    assert!(snapshots.iter().all(|snapshot| snapshot.is_reachable));
}

#[test]
fn failed_refresh_marks_previous_count_stale_and_no_reply_offline() {
    let now = Arc::new(Mutex::new(SystemTime::now()));
    let clock = Arc::clone(&now);
    let server = population_server();
    let service = ServerHealthService::with_clock(
        server.transport(),
        Arc::new(FixedProbe(None)),
        move || *clock.lock().unwrap(),
    );
    let first = service
        .check("localhost", 9000, "Busy", &CancelToken::none())
        .unwrap();
    assert!(!first.is_player_count_stale);
    *now.lock().unwrap() += Duration::from_secs(120);
    server.add_route(POPULATION, Route::Fail);
    let stale = service
        .check("localhost", 9000, "Busy", &CancelToken::none())
        .unwrap();
    assert_eq!(stale.player_count, Some(27));
    assert!(stale.is_player_count_stale);
    assert!(!stale.is_reachable);
}

#[test]
fn caller_cancellation_propagates() {
    let service = ServerHealthService::new(
        population_server().transport(),
        Arc::new(FixedProbe(Some(1.0))),
    );
    let cancel = CancelToken::new();
    cancel.cancel();
    assert!(service.check("localhost", 9000, "Busy", &cancel).is_err());
}

#[test]
fn ignores_invalid_population_values() {
    let counts = parse_player_counts(
        r#"
        [{"server":"Empty","count":0},{"server":"Bad","count":-1},
         {"server":"String","count":"12"},{"server":"Overflow","count":99999999999},null]
        "#,
    )
    .unwrap();
    assert_eq!(counts.len(), 1);
    assert_eq!(counts["empty"].1, 0);
}

#[test]
fn accepts_challenge_and_rejects_truncated_or_unrelated_datagrams() {
    let mut packet = vec![0u8; 52];
    packet[4..8].copy_from_slice(&0x40000u32.to_le_bytes());
    packet[16..18].copy_from_slice(&32u16.to_le_bytes());
    assert!(is_status_reply(&packet));
    assert!(!is_status_reply(&packet[..24]));
    packet[4] = 4;
    assert!(!is_status_reply(&packet));
    assert!(!is_status_reply(&[1, 2, 3]));
}

// --- KnownServerCatalogTests ----------------------------------------------------------
// OpenAC `tests/AcDream.Launcher.Core.Tests/Status/KnownServerCatalogTests.cs`.

mod known_servers {
    use std::sync::{Arc, Condvar, Mutex};
    use std::time::Duration;

    use ac_launcher_core::cancel::CancelToken;
    use ac_launcher_core::net::{HttpError, HttpRequest, HttpResponse, HttpTransport};
    use ac_launcher_core::status::known_servers::parse;
    use ac_launcher_core::status::{KnownServerCatalog, POPULATION_URI, SERVERS_URI};

    use super::common::TempDir;
    use super::common::fixtures::{FakeServer, Route};

    const SERVERS_JSON: &str = r#"
        [
          { "name": "Coldeve", "description": "A PvE server", "type": "PvE", "software": "ACE",
            "host": "play.coldeve.ac", "port": "9000", "website_url": "https://coldeve.ac/", "discord_url": "https://discord.gg/x" },
          { "name": "AChard", "type": "PvP", "software": "ACE", "host": "a-chard.ddns.net", "port": "9000",
            "website_url": "javascript:alert(1)",
            "players": { "count": 12, "updated_at": "2026-09-19 18:14:26 UTC", "age": "a week ago" } },
          { "name": "NoPort", "host": "example.org" },
          { "name": "BadPort", "host": "example.org", "port": "99999" },
          { "host": "nameless.example", "port": "9000" },
          { "name": "Numeric", "host": "numeric.example", "port": 9050, "unexpected": [1, 2] }
        ]
        "#;

    const COUNTS_JSON: &str =
        r#"[{ "server": "coldeve", "count": 675 }, { "server": "AChard", "count": 3 }]"#;

    fn none() -> CancelToken {
        CancelToken::none()
    }

    fn online(server: &FakeServer) {
        server.add(SERVERS_URI, SERVERS_JSON.as_bytes());
        server.add(POPULATION_URI, COUNTS_JSON.as_bytes());
    }

    fn catalog(server: &FakeServer, cache: &TempDir) -> KnownServerCatalog {
        KnownServerCatalog::new(server.transport(), &cache.join("cache"))
    }

    #[test]
    fn parse_keeps_usable_servers_sorted_by_name_with_their_counts_and_links() {
        let servers = parse(SERVERS_JSON, Some(COUNTS_JSON));
        let names: Vec<&str> = servers.iter().map(|server| server.name.as_str()).collect();
        assert_eq!(names, ["AChard", "Coldeve", "Numeric"]);
        let coldeve = &servers[1];
        assert_eq!(
            (
                coldeve.host.as_str(),
                coldeve.port,
                coldeve.server_type.as_deref(),
                coldeve.software.as_deref(),
                coldeve.description.as_deref()
            ),
            (
                "play.coldeve.ac",
                9000,
                Some("PvE"),
                Some("ACE"),
                Some("A PvE server")
            )
        );
        assert_eq!(
            coldeve.website.as_ref().map(ToString::to_string).as_deref(),
            Some("https://coldeve.ac/")
        );
        assert_eq!(
            coldeve.discord.as_ref().map(ToString::to_string).as_deref(),
            Some("https://discord.gg/x")
        );
        assert_eq!(coldeve.player_count, Some(675));
        assert_eq!(servers[0].player_count, Some(3));
        assert!(servers[0].website.is_none());
        assert_eq!(servers[2].port, 9050);
        assert_eq!(servers[2].player_count, None);
    }

    #[test]
    fn without_counts_a_server_keeps_the_count_its_own_entry_reports() {
        let servers = parse(SERVERS_JSON, Some("not json"));
        let find = |name: &str| servers.iter().find(|server| server.name == name).unwrap();
        assert_eq!(find("AChard").player_count, Some(12));
        assert_eq!(find("Coldeve").player_count, None);
    }

    #[test]
    fn a_fetched_list_is_saved_and_still_opens_offline() {
        let cache = TempDir::new("known-servers");
        let server = FakeServer::new();
        online(&server);
        let fetched = catalog(&server, &cache).load(&none()).unwrap().unwrap();
        assert!(!fetched.is_from_cache);

        let offline = FakeServer::new();
        offline.add_route(SERVERS_URI, Route::Fail);
        offline.add_route(POPULATION_URI, Route::Fail);
        let cached = catalog(&offline, &cache).load(&none()).unwrap().unwrap();
        assert!(cached.is_from_cache);
        assert_eq!(
            cached
                .servers
                .iter()
                .find(|server| server.name == "Coldeve")
                .unwrap()
                .player_count,
            Some(675)
        );
    }

    #[test]
    fn offline_with_nothing_saved_there_is_no_list() {
        let cache = TempDir::new("known-servers-none");
        let offline = FakeServer::new();
        offline.add_route(SERVERS_URI, Route::Fail);
        offline.add_route(POPULATION_URI, Route::Fail);
        assert!(catalog(&offline, &cache).load(&none()).unwrap().is_none());
    }

    #[test]
    fn an_unreadable_response_never_replaces_the_saved_list() {
        let cache = TempDir::new("known-servers-html");
        let server = FakeServer::new();
        online(&server);
        catalog(&server, &cache).load(&none()).unwrap();

        let broken = FakeServer::new();
        broken.add(SERVERS_URI, b"<html>maintenance</html>");
        broken.add(POPULATION_URI, b"<html>maintenance</html>");
        let list = catalog(&broken, &cache).load(&none()).unwrap().unwrap();
        assert!(list.is_from_cache);
        assert_eq!(list.servers.len(), 3);
    }

    #[test]
    fn odd_values_are_read_or_left_out_without_failing_the_list() {
        let servers = r#"
            [
              { "name": "Text", "host": "text.example", "port": " 9001 ", "players": { "count": "12" } },
              { "name": "Nulls", "host": "nulls.example", "port": 9000, "type": null, "description": null,
                "website_url": null, "players": { "count": null } },
              { "name": "Fraction", "host": "f.example", "port": 9000.5, "players": { "count": 1.5 } },
              { "name": "Negative", "host": "n.example", "port": "9000", "players": { "count": -3 } },
              { "name": null, "host": "x.example", "port": "9000" },
              { "name": "Array", "host": ["a"], "port": "9000" },
              "not an object",
              { "name": "PlayersText", "host": "p.example", "port": "9000", "players": "many" }
            ]
            "#;
        let counts = r#"
            [
              { "server": "Nulls", "count": "7" },
              { "server": "Negative", "count": null },
              { "server": 5, "count": 1 },
              "junk"
            ]
            "#;
        let parsed = parse(servers, Some(counts));
        let names: Vec<&str> = parsed.iter().map(|server| server.name.as_str()).collect();
        assert_eq!(names, ["Negative", "Nulls", "PlayersText", "Text"]);
        let find = |name: &str| parsed.iter().find(|server| server.name == name).unwrap();
        assert_eq!(find("Text").port, 9001);
        assert_eq!(find("Text").player_count, Some(12));
        assert_eq!(find("Nulls").player_count, Some(7));
        assert_eq!(find("Negative").player_count, None);
        assert_eq!(find("PlayersText").player_count, None);
        assert_eq!(find("Nulls").server_type, None);
    }

    #[test]
    fn a_list_that_is_not_a_server_list_never_replaces_the_saved_one() {
        for downloaded in [r#"{"error":"maintenance"}"#, "[]", r#"[{"name":"NoHost"}]"#] {
            let cache = TempDir::new("known-servers-not-a-list");
            let server = FakeServer::new();
            online(&server);
            catalog(&server, &cache).load(&none()).unwrap();

            let broken = FakeServer::new();
            broken.add(SERVERS_URI, downloaded.as_bytes());
            broken.add(POPULATION_URI, COUNTS_JSON.as_bytes());
            let list = catalog(&broken, &cache).load(&none()).unwrap().unwrap();
            assert!(list.is_from_cache, "{downloaded}");
            assert_eq!(list.servers.len(), 3, "{downloaded}");
        }
    }

    /// Answers the server list only once the player counts were asked for.
    #[derive(Default)]
    struct Together {
        counts_requested: Mutex<bool>,
        signal: Condvar,
    }

    impl HttpTransport for Together {
        fn get(
            &self,
            request: &HttpRequest,
            _cancel: &CancelToken,
        ) -> Result<HttpResponse, HttpError> {
            let url = request.url.clone();
            if url.to_string() == POPULATION_URI {
                *self.counts_requested.lock().unwrap() = true;
                self.signal.notify_all();
                return Ok(HttpResponse::with_body(
                    url,
                    200,
                    COUNTS_JSON.as_bytes().to_vec(),
                ));
            }
            let requested = self.counts_requested.lock().unwrap();
            let (requested, _) = self
                .signal
                .wait_timeout_while(requested, Duration::from_secs(5), |requested| !*requested)
                .unwrap();
            if !*requested {
                return Err(HttpError::TimedOut);
            }
            Ok(HttpResponse::with_body(
                url,
                200,
                SERVERS_JSON.as_bytes().to_vec(),
            ))
        }
    }

    #[test]
    fn the_list_and_the_player_counts_download_together() {
        let cache = TempDir::new("known-servers-together");
        let catalog = KnownServerCatalog::new(Arc::new(Together::default()), &cache.join("cache"));
        let list = catalog.load(&none()).unwrap().unwrap();
        assert!(!list.is_from_cache);
        assert_eq!(
            list.servers
                .iter()
                .find(|server| server.name == "Coldeve")
                .unwrap()
                .player_count,
            Some(675)
        );
    }

    #[test]
    fn a_recent_list_is_reused_without_another_fetch() {
        let cache = TempDir::new("known-servers-reuse");
        let server = FakeServer::new();
        online(&server);
        let catalog = catalog(&server, &cache);
        catalog.load(&none()).unwrap();
        catalog.load(&none()).unwrap();
        assert_eq!(server.requests().len(), 2);
    }
}
