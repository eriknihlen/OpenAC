//! Status-file lines.
//!
//! Ported from OpenAC `tests/AcDream.Runtime.Tests/Session/SessionStatusWriterTests.cs`.
//! Lines are checked with this crate's strict JSON reader.

mod common;

use std::fs::OpenOptions;
use std::io::{BufRead, BufReader};
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::{Duration, UNIX_EPOCH};

use ac_host::json::{self, Node, Value};
use ac_host::status::format_status_line;
use ac_host::{FileSink, RosterEntry, RosterReport, ServerRefusalNote, StatusWriter, WriterSink};
use common::{TempDir, TempFile};

fn parse(line: &str) -> Node {
    json::parse(line.as_bytes()).expect("a status line is JSON")
}

fn names(node: &Node) -> Vec<String> {
    match &node.value {
        Value::Object(members) => members.iter().map(|m| m.name.clone()).collect(),
        _ => panic!("object expected"),
    }
}

fn get<'a>(node: &'a Node, name: &str) -> &'a Value {
    match &node.value {
        Value::Object(members) => {
            &members
                .iter()
                .find(|m| m.name == name)
                .unwrap_or_else(|| panic!("missing {name}"))
                .value
                .value
        }
        _ => panic!("object expected"),
    }
}

fn text(node: &Node, name: &str) -> String {
    match get(node, name) {
        Value::String(text) => text.clone(),
        other => panic!("{name} is not a string: {other:?}"),
    }
}

fn number(node: &Node, name: &str) -> i64 {
    match get(node, name) {
        Value::Number(text) => text.parse().unwrap(),
        other => panic!("{name} is not a number: {other:?}"),
    }
}

fn event(line: &str) -> String {
    text(&parse(line), "e")
}

fn read_lines(path: &Path) -> Vec<String> {
    let file = OpenOptions::new().read(true).open(path).unwrap();
    BufReader::new(file).lines().map(Result::unwrap).collect()
}

fn roster(entries: Vec<RosterEntry>) -> RosterReport {
    RosterReport {
        account_name: "account".into(),
        slot_count: 11,
        entries,
    }
}

fn entry(id: u32, name: &str, seconds: u32) -> RosterEntry {
    RosterEntry {
        id,
        name: name.into(),
        seconds_greyed_out: seconds,
    }
}

/// A writer into a shared buffer, with a fixed clock.
fn memory_writer() -> (StatusWriter<WriterSink<SharedBuffer>>, SharedBuffer) {
    let buffer = SharedBuffer::default();
    let writer = StatusWriter::new(Some(WriterSink(buffer.clone())))
        .with_clock(|| UNIX_EPOCH + Duration::new(1_791_800_430, 123_456_700));
    (writer, buffer)
}

#[derive(Clone, Default)]
struct SharedBuffer(Arc<Mutex<Vec<u8>>>);

impl SharedBuffer {
    fn lines(&self) -> Vec<String> {
        let bytes = self.0.lock().unwrap().clone();
        String::from_utf8(bytes)
            .unwrap()
            .lines()
            .map(str::to_owned)
            .collect()
    }
}

impl std::io::Write for SharedBuffer {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        self.0.lock().unwrap().extend_from_slice(bytes);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

#[test]
fn each_event_writes_the_exact_pinned_shape_in_order() {
    let file = TempFile::reserve("status", ".jsonl");
    let mut writer = StatusWriter::for_file(Some(&file.path));

    writer.started("s1");
    writer.connected("s1");
    writer.character_list(
        "s1",
        &roster(vec![
            entry(0x5000_0001, "Ready", 0),
            entry(0x5000_0002, "Grey", 10),
        ]),
    );
    writer.entered_world("s1", 0x5000_0001, "Ready");
    writer.plugin_loaded("s1", "acdream.good");
    writer.plugin_failed("s1", "acdream.bad", "enable failed");
    writer.login_command_failed("s1", 2, "/version", "unsupported headless command");
    writer.disconnected("s1", "stopped");
    writer.exited("s1", 0, "disposed");
    writer.close();

    let lines = read_lines(&file.path);
    assert_eq!(lines.len(), 9);

    let started = parse(&lines[0]);
    assert_eq!(number(&started, "v"), 1);
    assert_eq!(text(&started, "e"), "started");
    assert!(!text(&started, "t").is_empty());
    assert_eq!(text(&started, "sessionId"), "s1");

    let connected = parse(&lines[1]);
    assert_eq!(text(&connected, "e"), "connected");
    assert_eq!(text(&connected, "sessionId"), "s1");

    let list = parse(&lines[2]);
    assert_eq!(text(&list, "e"), "characterList");
    assert_eq!(text(&list, "accountName"), "account");
    assert_eq!(number(&list, "slotCount"), 11);
    let Value::Array(characters) = get(&list, "characters") else {
        panic!("characters array expected");
    };
    assert_eq!(characters.len(), 2);
    assert_eq!(number(&characters[0], "id"), 0x5000_0001);
    assert_eq!(text(&characters[0], "name"), "Ready");
    assert_eq!(number(&characters[0], "secondsGreyedOut"), 0);
    assert_eq!(number(&characters[1], "secondsGreyedOut"), 10);

    let entered = parse(&lines[3]);
    assert_eq!(text(&entered, "e"), "enteredWorld");
    assert_eq!(number(&entered, "characterId"), 0x5000_0001);
    assert_eq!(text(&entered, "characterName"), "Ready");

    let loaded = parse(&lines[4]);
    assert_eq!(text(&loaded, "e"), "pluginLoaded");
    assert_eq!(text(&loaded, "plugin"), "acdream.good");

    let failed = parse(&lines[5]);
    assert_eq!(text(&failed, "e"), "pluginFailed");
    assert_eq!(text(&failed, "plugin"), "acdream.bad");
    assert_eq!(text(&failed, "error"), "enable failed");

    let command = parse(&lines[6]);
    assert_eq!(number(&command, "v"), 1);
    assert_eq!(text(&command, "e"), "loginCommandFailed");
    assert_eq!(text(&command, "sessionId"), "s1");
    assert_eq!(number(&command, "commandIndex"), 2);
    assert_eq!(text(&command, "command"), "/version");
    assert_eq!(text(&command, "error"), "unsupported headless command");
    assert_eq!(
        names(&command),
        [
            "v",
            "e",
            "t",
            "sessionId",
            "commandIndex",
            "command",
            "error"
        ]
    );

    let disconnected = parse(&lines[7]);
    assert_eq!(text(&disconnected, "e"), "disconnected");
    assert_eq!(text(&disconnected, "reason"), "stopped");

    let exited = parse(&lines[8]);
    assert_eq!(text(&exited, "e"), "exited");
    assert_eq!(number(&exited, "code"), 0);
    assert_eq!(text(&exited, "reason"), "disposed");
}

#[test]
fn no_op_writer_never_creates_a_file() {
    let file = TempFile::reserve("status-noop", ".jsonl");
    let mut writer = StatusWriter::for_file(None);

    writer.started("s1");
    writer.connected("s1");
    writer.plugin_loaded("s1", "acdream.good");
    writer.plugin_failed("s1", "acdream.bad", "failed");
    writer.login_command_failed("s1", 0, "", "unknown command");
    writer.character_created("s1", 0x5000_0001, "NewChar");
    writer.creation_failed("s1", 3, "NameInUse", "Bob");
    writer.disconnected("s1", "stopped");
    writer.exited("s1", 0, "disposed");

    assert!(!writer.is_enabled());
    assert!(!file.path.exists());
}

#[test]
fn character_created_and_creation_failed_write_the_pinned_shape() {
    let (mut writer, buffer) = memory_writer();

    writer.character_created("s1", 0x5000_0010, "NewChar");
    writer.creation_failed("s1", 3, "NameInUse", "Bob");

    let lines = buffer.lines();
    assert_eq!(lines.len(), 2);
    let created = parse(&lines[0]);
    assert_eq!(number(&created, "v"), 1);
    assert_eq!(text(&created, "e"), "characterCreated");
    assert_eq!(text(&created, "sessionId"), "s1");
    assert_eq!(number(&created, "guid"), 0x5000_0010);
    assert_eq!(text(&created, "name"), "NewChar");
    assert_eq!(
        names(&created),
        ["v", "e", "t", "sessionId", "guid", "name"]
    );

    let failed = parse(&lines[1]);
    assert_eq!(text(&failed, "e"), "creationFailed");
    assert_eq!(number(&failed, "code"), 3);
    assert_eq!(text(&failed, "reason"), "NameInUse");
    assert_eq!(text(&failed, "name"), "Bob");
    assert_eq!(
        names(&failed),
        ["v", "e", "t", "sessionId", "code", "reason", "name"]
    );
}

#[test]
fn blank_path_is_treated_as_absent() {
    let mut writer = StatusWriter::for_file(Some(Path::new("   ")));
    assert!(!writer.is_enabled());
    writer.started("s1");
}

#[test]
fn second_connected_edge_first_closes_the_retiring_connection() {
    let (mut writer, buffer) = memory_writer();

    writer.connected("s1");
    writer.connected("s1");

    let lines = buffer.lines();
    let events: Vec<_> = lines.iter().map(|line| event(line)).collect();
    assert_eq!(events, ["connected", "disconnected", "connected"]);
    assert_eq!(text(&parse(&lines[1]), "reason"), "reconnect");
}

// Not an OpenAC test: the server's refusal on `exited` and the heartbeat
// are additions to its vocabulary, for the launcher's plain-words reason
// and its not-responding check.
#[test]
fn exited_names_the_servers_refusal_and_heartbeats_carry_only_the_envelope() {
    let (mut writer, buffer) = memory_writer();

    writer.heartbeat("s1");
    writer.exited_by(
        "s1",
        6,
        "the server ended the session",
        Some(ServerRefusalNote {
            message: "Another account is already logged on from this client.",
            account_in_use: true,
        }),
    );
    writer.heartbeat("s1");

    let lines = buffer.lines();
    assert_eq!(lines.len(), 2, "nothing is written after exited");
    let heartbeat = parse(&lines[0]);
    assert_eq!(text(&heartbeat, "e"), "heartbeat");
    assert_eq!(names(&heartbeat), ["v", "e", "t", "sessionId"]);
    let exited = parse(&lines[1]);
    assert_eq!(
        names(&exited),
        [
            "v",
            "e",
            "t",
            "sessionId",
            "code",
            "reason",
            "serverMessage",
            "accountInUse"
        ]
    );
    assert_eq!(
        text(&exited, "serverMessage"),
        "Another account is already logged on from this client."
    );

    // A plain exit keeps OpenAC's shape.
    let (mut writer, buffer) = memory_writer();
    writer.exited_by(
        "s1",
        6,
        "booted",
        Some(ServerRefusalNote {
            message: "The server disconnected.",
            account_in_use: false,
        }),
    );
    assert_eq!(
        names(&parse(&buffer.lines()[0])),
        [
            "v",
            "e",
            "t",
            "sessionId",
            "code",
            "reason",
            "serverMessage"
        ]
    );
    let (mut writer, buffer) = memory_writer();
    writer.exited("s1", 0, "graceful");
    assert_eq!(
        names(&parse(&buffer.lines()[0])),
        ["v", "e", "t", "sessionId", "code", "reason"]
    );
}

#[test]
fn exited_is_idempotent_terminal_and_closes_an_open_connection() {
    let (mut writer, buffer) = memory_writer();

    writer.connected("s1");
    writer.exited("s1", 0, "graceful");
    writer.exited("s1", 1, "duplicate-must-not-win");
    writer.connected("s1");

    let lines = buffer.lines();
    let events: Vec<_> = lines.iter().map(|line| event(line)).collect();
    assert_eq!(events, ["connected", "disconnected", "exited"]);
    assert_eq!(text(&parse(&lines[1]), "reason"), "process-exit");
    assert_eq!(number(&parse(&lines[2]), "code"), 0);
    assert_eq!(text(&parse(&lines[2]), "reason"), "graceful");
}

#[test]
fn each_event_serializes_exactly_its_pinned_property_set_and_nothing_else() {
    let (mut writer, buffer) = memory_writer();

    writer.started("bot");
    writer.connected("bot");
    writer.character_list(
        "bot",
        &RosterReport {
            account_name: "account-name".into(),
            slot_count: 11,
            entries: vec![entry(0x5000_0001, "Ready", 0)],
        },
    );
    writer.entered_world("bot", 0x5000_0001, "Ready");
    writer.plugin_loaded("bot", "acdream.good");
    writer.plugin_failed("bot", "acdream.bad", "enable failed");
    writer.login_command_failed("bot", 1, "/version", "unsupported");
    writer.disconnected("bot", "stopped");
    writer.exited("bot", 0, "disposed");

    let lines = buffer.lines();
    assert_eq!(lines.len(), 9);
    let expected: [&[&str]; 9] = [
        &["v", "e", "t", "sessionId"],
        &["v", "e", "t", "sessionId"],
        &[
            "v",
            "e",
            "t",
            "sessionId",
            "accountName",
            "slotCount",
            "characters",
        ],
        &["v", "e", "t", "sessionId", "characterId", "characterName"],
        &["v", "e", "t", "sessionId", "plugin"],
        &["v", "e", "t", "sessionId", "plugin", "error"],
        &[
            "v",
            "e",
            "t",
            "sessionId",
            "commandIndex",
            "command",
            "error",
        ],
        &["v", "e", "t", "sessionId", "reason"],
        &["v", "e", "t", "sessionId", "code", "reason"],
    ];
    for (line, expected) in lines.iter().zip(expected) {
        assert_eq!(names(&parse(line)), expected, "{line}");
    }
    let list = parse(&lines[2]);
    let Value::Array(characters) = get(&list, "characters") else {
        panic!("characters array expected");
    };
    assert_eq!(names(&characters[0]), ["id", "name", "secondsGreyedOut"]);
}

#[test]
fn a_line_is_byte_exact() {
    let line = format_status_line(
        "s1",
        &ac_host::status::StatusEvent::EnteredWorld {
            character_id: 0x5000_0001,
            character_name: "Bob's <toon>",
        },
        UNIX_EPOCH + Duration::new(1_791_800_430, 123_456_700),
    );
    assert_eq!(
        line,
        "{\"v\":1,\"e\":\"enteredWorld\",\"t\":\"2026-10-12T10:20:30.1234567+00:00\",\
         \"sessionId\":\"s1\",\"characterId\":1342177281,\
         \"characterName\":\"Bob\\u0027s \\u003Ctoon\\u003E\"}"
    );
}

#[test]
fn window_lines_are_byte_exact() {
    // An addition over OpenAC: the launcher follows the
    // client's game window through these three events.
    let at = UNIX_EPOCH + Duration::new(1_791_800_430, 123_456_700);
    let line = |event| format_status_line("s1", &event, at);
    let envelope = |e: &str| {
        format!(
            "{{\"v\":1,\"e\":\"{e}\",\"t\":\"2026-10-12T10:20:30.1234567+00:00\",\"sessionId\":\"s1\""
        )
    };
    assert_eq!(
        line(ac_host::status::StatusEvent::WindowOpened),
        format!("{}}}", envelope("windowOpened"))
    );
    assert_eq!(
        line(ac_host::status::StatusEvent::WindowClosed {
            reason: "closedByUser"
        }),
        format!("{},\"reason\":\"closedByUser\"}}", envelope("windowClosed"))
    );
    assert_eq!(
        line(ac_host::status::StatusEvent::WindowFailed { error: "no GPU" }),
        format!("{},\"error\":\"no GPU\"}}", envelope("windowFailed"))
    );
}

#[test]
fn missing_parent_directory_is_created_and_events_flow() {
    let root = TempDir::new("status-root");
    let path = root
        .path
        .join("nested")
        .join("sessions")
        .join("s1")
        .join("status.jsonl");
    assert!(!path.parent().unwrap().exists());
    let mut writer = StatusWriter::for_file(Some(&path));

    writer.started("s1");
    writer.connected("s1");

    assert!(writer.is_enabled());
    let lines = read_lines(&path);
    assert_eq!(lines.len(), 2);
    assert!(lines[0].contains("\"started\""));
    assert!(lines[1].contains("\"connected\""));
    writer.close();
}

#[test]
fn parent_segment_is_a_file_reports_instead_of_failing() {
    let blocker = TempFile::with("status-blocker", "not a directory");
    let path = blocker.path.join("status.jsonl");
    let mut writer = StatusWriter::for_file(Some(&path)).with_diagnostic(|_| {});
    assert!(writer.is_enabled());

    writer.started("s1");
    writer.connected("s1");
    writer.exited("s1", 0, "disposed");
}

#[test]
fn a_concurrent_tailer_can_read_while_the_writer_appends() {
    let file = TempFile::reserve("status-tail", ".jsonl");
    let mut writer = StatusWriter::for_file(Some(&file.path));
    writer.started("s1");

    let tailer = OpenOptions::new().read(true).open(&file.path).unwrap();
    let mut reader = BufReader::new(tailer);
    let mut first = String::new();
    reader.read_line(&mut first).unwrap();
    assert!(first.contains("\"started\""));

    // The writer keeps working while the tailer's handle is still open.
    writer.connected("s1");
    let mut second = String::new();
    reader.read_line(&mut second).unwrap();
    assert!(second.contains("\"connected\""));
    writer.close();
}

#[test]
fn close_releases_the_file_for_deletion() {
    let file = TempFile::reserve("status-close", ".jsonl");
    let mut writer = StatusWriter::for_file(Some(&file.path));

    writer.started("s1");
    writer.exited("s1", 0, "graceful");
    writer.close();
    assert!(!writer.is_enabled());

    std::fs::remove_file(&file.path).unwrap();
    assert!(!file.path.exists());
    writer.started("s1");
    assert!(!file.path.exists());
}

#[test]
fn a_failing_destination_is_reported_once_until_writes_recover() {
    let blocker = TempFile::with("status-blocker", "not a directory");
    let path = blocker.path.join("status.jsonl");
    let reports = Arc::new(Mutex::new(Vec::<String>::new()));
    let sink = Arc::clone(&reports);
    let mut writer = StatusWriter::for_file(Some(&path))
        .with_diagnostic(move |message| sink.lock().unwrap().push(message.to_owned()));

    writer.started("s1");
    writer.connected("s1");
    writer.disconnected("s1", "stopped");

    // Three impossible writes, one complaint, and the writer still tries.
    let reports = reports.lock().unwrap();
    assert_eq!(reports.len(), 1);
    assert!(reports[0].starts_with("[status-writer] could not write to"));
    assert!(writer.is_enabled());
}

#[cfg(windows)]
mod windows_sharing {
    use super::*;
    use std::os::windows::fs::OpenOptionsExt;

    const FILE_SHARE_READ: u32 = 0x1;

    /// Opens the file the way an ordinary reader does: asking that nobody
    /// write while it reads.
    fn exclusive_reader(path: &Path) -> std::io::Result<std::fs::File> {
        OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .open(path)
    }

    #[test]
    fn an_ordinary_reader_cannot_silence_the_stream() {
        let file = TempFile::reserve("status-onlooker", ".jsonl");
        let mut writer = StatusWriter::for_file(Some(&file.path));
        writer.started("s1");

        // The platform refuses the onlooker, never the session.
        let onlooker = exclusive_reader(&file.path);
        writer.connected("s1");
        writer.entered_world("s1", 0x5000_0001, "Ready");
        writer.exited("s1", 0, "graceful");
        drop(onlooker);
        writer.close();

        assert_eq!(
            read_lines(&file.path)
                .iter()
                .map(|line| event(line))
                .collect::<Vec<_>>(),
            [
                "started",
                "connected",
                "enteredWorld",
                "disconnected",
                "exited"
            ]
        );
    }

    #[test]
    fn a_write_that_fails_is_not_the_end_of_the_stream() {
        let file = TempFile::with("status-held", "");
        let mut writer = StatusWriter::for_file(Some(&file.path)).with_diagnostic(|_| {});

        {
            let _held = exclusive_reader(&file.path).unwrap();
            writer.started("s1");
        }

        writer.connected("s1");
        writer.exited("s1", 0, "graceful");
        writer.close();

        assert!(writer.sink().is_some());
        let events: Vec<_> = read_lines(&file.path).iter().map(|l| event(l)).collect();
        assert!(events.contains(&"connected".to_owned()));
        assert!(events.contains(&"exited".to_owned()));
    }
}

#[test]
fn file_sink_paths_are_absolute() {
    let sink = FileSink::new(Path::new("relative-status.jsonl"));
    assert!(sink.path().is_absolute());
}
