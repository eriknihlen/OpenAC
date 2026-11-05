//! Plugins inside a whole session, against a loopback stand-in for the
//! server: they load when the session starts, answer their verbs typed into
//! the chat box, hear a window attach and leave without the session
//! noticing, keep ticking, and stop (last loaded first) when the session
//! ends. The same calls answer with and without the window.

use std::net::UdpSocket;
use std::sync::mpsc;

use ac_bits::Writer;
use ac_net::crypto::Isaac;
use ac_net::packet::{PacketFlags, PacketHeader, encode, parse_packet, write_single_fragment};
use ac_plugin::fixtures::{fresh_root, install};
use ac_plugin::{HostVersion, PluginEngine, PluginReport, PluginSessionOptions};

use super::test_server::{bind_pair, recv_from_client, recv_login};
use super::*;
use crate::plugins::{PluginEvent, PluginSetup};

const SERVER_SEED: u32 = 0x1122_3344;
const CLIENT_SEED: u32 = 0x5566_7788;
const PLAYER: u32 = 0x5000_000B;

fn connect_request() -> Vec<u8> {
    let mut w = Writer::new();
    w.f64(1000.0);
    w.u64(0xFEED_FACE_CAFE_BABE);
    w.u32(0x1234);
    w.u32(SERVER_SEED);
    w.u32(CLIENT_SEED);
    w.u32(0);
    let header = PacketHeader {
        flags: PacketFlags::CONNECT_REQUEST,
        iteration: 7,
        ..PacketHeader::default()
    };
    encode(header, w.as_slice(), None).unwrap()
}

fn character_list() -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF658);
    w.u32(0);
    w.u32(1);
    w.u32(PLAYER);
    w.string16l("+Plugged").unwrap();
    w.u32(0);
    w.u32(0);
    w.i32(11);
    w.string16l("testaccount2").unwrap();
    w.u32(1);
    w.u32(1);
    w.into_vec()
}

/// Local speech (`HearSpeech`): text, speaker, speaker id, chat type.
fn hear_speech(sender: &str, text: &str) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0x02BB);
    w.string16l(text).unwrap();
    w.string16l(sender).unwrap();
    w.u32(0x5000_0099);
    w.u32(2);
    w.into_vec()
}

fn words(words: &[u32]) -> Vec<u8> {
    words.iter().flat_map(|w| w.to_le_bytes()).collect()
}

struct Server {
    socket: UdpSocket,
    client: std::net::SocketAddr,
    keystream: Isaac,
    sequence: u32,
    fragment_sequence: u32,
}

impl Server {
    fn send(&mut self, body: &[u8]) {
        let mut fragment = Vec::new();
        write_single_fragment(&mut fragment, self.fragment_sequence, 9, body).unwrap();
        self.fragment_sequence += 1;
        let header = PacketHeader {
            sequence: self.sequence,
            flags: PacketFlags::BLOB_FRAGMENTS | PacketFlags::ENCRYPTED_CHECKSUM,
            ..PacketHeader::default()
        };
        self.sequence += 1;
        let datagram = encode(header, &fragment, Some(&mut self.keystream)).unwrap();
        self.socket.send_to(&datagram, self.client).unwrap();
    }

    fn wait_for(&self, opcode: u32, within: Duration) {
        let deadline = Instant::now() + within;
        let mut buf = [0u8; 2048];
        while let Some(len) = recv_from_client(&self.socket, &mut buf, self.client, deadline) {
            let Ok(packet) = parse_packet(&buf[..len]) else {
                continue;
            };
            if packet
                .fragments()
                .any(|f| f.header.index == 0 && f.payload.get(..4) == Some(&opcode.to_le_bytes()))
            {
                return;
            }
        }
        panic!("the client never sent {opcode:#06x}");
    }
}

/// What the test reads off the session's events.
#[derive(Default)]
struct Seen {
    chat: Vec<String>,
    logs: Vec<(String, String)>,
    reports: Vec<PluginReport>,
    /// Chat lines and plugin log lines together, in the order they came.
    order: Vec<String>,
}

impl Seen {
    fn take(&mut self, event: HostEvent) -> Option<SessionSummary> {
        match event {
            HostEvent::Chat(line) => {
                self.order.push(line.text.clone());
                self.chat.push(line.text);
            }
            HostEvent::Plugin(PluginEvent::Log {
                plugin, message, ..
            }) => {
                self.order.push(message.clone());
                self.logs.push((plugin, message));
            }
            HostEvent::Plugin(PluginEvent::Report(report)) => self.reports.push(report),
            HostEvent::Ended(summary) => return Some(summary),
            _ => {}
        }
        None
    }
}

/// Reads events until `done` holds (or the session ends).
/// Runs `/probe census` and returns its lines (`probe census <read>: ...`).
fn census(
    handle: &SessionHandle,
    commands: &mpsc::Sender<SessionCommand>,
    seen: &mut Seen,
) -> Vec<String> {
    let start = seen.logs.len();
    commands
        .send(SessionCommand::Chat("/probe census".into()))
        .unwrap();
    until(handle, seen, |s| {
        s.logs[start..]
            .iter()
            .any(|(_, m)| m.starts_with("probe census storage_read_text:"))
    });
    seen.logs[start..]
        .iter()
        .filter(|(plugin, m)| plugin == "openac.probe" && m.starts_with("probe census "))
        .map(|(_, m)| m.clone())
        .collect()
}

/// The parity rule (OpenAC `HostParity` transcripts compared across the
/// windowed and windowless arms): two census runs read the same, line for
/// line, except a read whose answer the import census says depends on a
/// window.
fn same_but_for_the_window(headless: &[String], windowed: &[String]) {
    assert_eq!(headless.len(), windowed.len(), "{headless:?}\n{windowed:?}");
    for (a, b) in headless.iter().zip(windowed) {
        let read = a
            .strip_prefix("probe census ")
            .and_then(|rest| rest.split_once(':'))
            .map_or("", |(read, _)| read);
        let window = ac_plugin::abi::Import::from_name(read).is_some_and(|i| i.needs_window());
        if !window {
            assert_eq!(a, b, "{read} reads differently with a window");
        }
    }
}

fn until(handle: &SessionHandle, seen: &mut Seen, done: impl Fn(&Seen) -> bool) {
    let deadline = Instant::now() + Duration::from_secs(20);
    while !done(seen) {
        assert!(Instant::now() < deadline, "timed out; chat {:?}", seen.chat);
        if let Some(event) = handle.next_event(Duration::from_millis(100))
            && let Some(summary) = seen.take(event)
        {
            panic!("the session ended early: {:?}", summary.error);
        }
    }
}

/// The loopback stand-in for the server: login, character list, enter the
/// world, and the log off when the session ends (with a line spoken while
/// it is under way that no plugin hears: the session is stopping).
fn fake_server() -> (std::net::SocketAddr, thread::JoinHandle<()>) {
    fake_server_saying("ping hello after the stop")
}

/// [`fake_server`], speaking `speech` between the client's log off request
/// and the server's confirmation.
fn fake_server_saying(speech: &'static str) -> (std::net::SocketAddr, thread::JoinHandle<()>) {
    // Bound and read as `super::test_server` says.
    let (login, connect, address) = bind_pair();
    let server = thread::spawn(move || {
        let mut buf = [0u8; 2048];
        let client = recv_login(&login, Instant::now() + Duration::from_secs(20))
            .expect("the login request");
        login.send_to(&connect_request(), client).unwrap();
        let within = Instant::now() + Duration::from_secs(5);
        recv_from_client(&connect, &mut buf, client, within).expect("the connect response");
        let mut server = Server {
            socket: login,
            client,
            keystream: Isaac::new(SERVER_SEED),
            sequence: 2,
            fragment_sequence: 1,
        };
        server.send(&words(&[0xF7E5]));
        server.send(&character_list());
        server.send(&words(&[0xF7EA]));
        server.wait_for(0xF7C8, Duration::from_secs(10));
        server.send(&words(&[0xF7DF]));
        server.wait_for(0xF657, Duration::from_secs(10));
        // The character's own object: the client says its login is
        // complete, and from then on the server listens.
        server.send(&words(&[0xF745, PLAYER, 0x11, 0, 0, 0, 0, 0, 0, 0]));
        // The log off, a line spoken while it is under way, and its
        // confirmation.
        server.wait_for(0xF653, Duration::from_secs(60));
        server.send(&hear_speech("Bystander", speech));
        server.send(&words(&[0xF653]));
    });
    (address, server)
}

/// A session entering the world as +Plugged with the plugins in `root`.
fn plugin_config(address: std::net::SocketAddr, root: &std::path::Path) -> SessionConfig {
    SessionConfig {
        server: address,
        account: "testaccount2".into(),
        password: "x".into(),
        character: CharacterSelector::Name("+Plugged".into()),
        loss: LossOptions::default(),
        enter_world: true,
        await_character_selection: false,
        data_directory: None,
        tables: None,
        content: None,
        diagnostics: NetDiagnostics::default(),
        login: LoginAutomation::default(),
        plugins: Some(PluginSetup {
            engine: Arc::new(PluginEngine::new()),
            peer_tags: Vec::new(),
            hotkey_overrides: None,
            peer_directory: None,
            options: PluginSessionOptions {
                roots: vec![root.to_path_buf()],
                allow_list: None,
                settings: ac_plugin::PluginSessionSettings::empty(),
                host_version: HostVersion::new(0, 1, 0),
                storage: ac_plugin::storage::StorageRoots::default(),
                slow_log: None,
            },
        }),
    }
}

#[test]
fn plugins_live_in_the_session_with_and_without_a_window() {
    let root = fresh_root("runner");
    install(&root, "hello");
    install(&root, "probe");

    let (address, server) = fake_server();
    let config = plugin_config(address, &root);
    let handle = spawn(config).unwrap();
    let commands = handle.commands();
    let mut seen = Seen::default();

    // Loaded at session start, before the connection is up.
    until(&handle, &mut seen, |s| s.reports.len() == 2);
    assert_eq!(
        seen.reports,
        vec![
            PluginReport::Loaded {
                id: "openac.hello".into()
            },
            PluginReport::Loaded {
                id: "openac.probe".into()
            },
        ]
    );
    assert!(seen.logs.contains(&(
        "openac.probe".into(),
        "probe enable: character=none label=- window=false".into()
    )));

    // In the world: a verb typed into the chat box reaches the plugin.
    until(&handle, &mut seen, |s| {
        s.logs.iter().any(|(_, m)| {
            m == "probe tick100: character=+Plugged 0x5000000B on  label=- window=false"
        })
    });
    commands
        .send(SessionCommand::Chat("/hello".into()))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.chat
            .iter()
            .any(|t| t.contains("Hello, +Plugged! (no window is attached)"))
    });

    // The parity harness: the probe's census (every read it
    // makes, summarised without run-to-run values) through this session's
    // own binding, headless now, then with the window and after it left.
    let headless = census(&handle, &commands, &mut seen);
    assert!(headless.len() > 60, "{headless:?}");

    // A window attaches: the plugins hear it, and what they post reaches
    // the window too.
    let (to_window, window) = mpsc::channel();
    commands.send(SessionCommand::Attach(to_window)).unwrap();
    until(&handle, &mut seen, |s| {
        s.logs
            .contains(&("openac.hello".into(), "hello: window attached: true".into()))
    });
    commands
        .send(SessionCommand::Chat("@HELLO".into()))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.chat
            .iter()
            .any(|t| t.contains("Hello, +Plugged! (a window is attached)"))
    });
    let drawn =
        std::iter::from_fn(|| window.recv_timeout(Duration::from_secs(2)).ok()).any(|event| {
            match event {
                PresentationEvent::Chat(lines) => lines
                    .iter()
                    .any(|l| l.text.contains("(a window is attached)")),
                _ => false,
            }
        });
    assert!(drawn, "the window shows the plugin's line");
    let windowed = census(&handle, &commands, &mut seen);
    same_but_for_the_window(&headless, &windowed);
    commands.send(SessionCommand::Detach).unwrap();
    until(&handle, &mut seen, |s| {
        s.logs.contains(&(
            "openac.hello".into(),
            "hello: window attached: false".into(),
        ))
    });
    assert_eq!(census(&handle, &commands, &mut seen), headless);

    // A plugin's submit is routed as a typed line after its call: a plugin
    // verb submitted by a plugin reaches its plugin.
    commands
        .send(SessionCommand::Chat("/hello say /probe".into()))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.logs.iter().any(|(_, m)| m.starts_with("probe command:"))
    });

    // The plugin manager answers in the transcript.
    commands
        .send(SessionCommand::Chat("/plugin".into()))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.chat
            .iter()
            .any(|t| t.contains("openac.probe 0.1.0 (Probe): loaded"))
    });

    // A plugin's chat filter: a line it drops never reaches the
    // transcript or the host; a later line still does, after it.
    let greetings = |s: &Seen| {
        s.chat
            .iter()
            .filter(|t| t.contains("Hello, +Plugged!"))
            .count()
    };
    let before = greetings(&seen);
    commands
        .send(SessionCommand::Chat("/hello filter +Plugged".into()))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.chat.iter().any(|t| t.contains("hello filter installed"))
    });
    commands
        .send(SessionCommand::Chat("/hello".into()))
        .unwrap();
    commands
        .send(SessionCommand::Chat("/hello ticks".into()))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.chat
            .iter()
            .any(|t| t.contains(" ticks") && t.contains("hello: "))
    });
    assert_eq!(
        greetings(&seen),
        before,
        "the filtered greeting never arrived"
    );
    commands
        .send(SessionCommand::Chat("/hello unfilter".into()))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.chat.iter().any(|t| t.contains("hello unfiltered: true"))
    });

    // The session ends: the plugins stop, the last loaded first, before
    // the session reports its end.
    commands.send(SessionCommand::Shutdown).unwrap();
    let summary = loop {
        let event = handle
            .next_event(Duration::from_secs(60))
            .expect("the session ends");
        if let Some(summary) = seen.take(event) {
            break summary;
        }
    };
    server.join().unwrap();
    handle.join();
    let _ = std::fs::remove_dir_all(&root);
    assert_eq!(summary.error, None);
    // The arrival is heard once the server listens (the character's own
    // object is there); the leaving is heard as the
    // session stops, with the world still readable, and nothing from the
    // server after it (OpenAC `AnnounceLeavingWorld` before the teardown).
    let lifecycle: Vec<&str> = seen
        .logs
        .iter()
        .map(|(_, m)| m.as_str())
        .filter(|m| m.starts_with("probe event:"))
        .collect();
    assert_eq!(
        lifecycle,
        [
            "probe event: login complete: character=+Plugged objects=1 owned=0",
            "probe event: logoff: character=+Plugged objects=1 owned=0",
        ]
    );
    assert!(
        seen.chat.iter().any(|t| t.contains("after the stop")),
        "the line was spoken: {:?}",
        seen.chat
    );
    assert!(
        !seen.chat.iter().any(|t| t.starts_with("hello heard")),
        "no plugin heard it: {:?}",
        seen.chat
    );
    let stops: Vec<&str> = seen
        .logs
        .iter()
        .map(|(_, m)| m.as_str())
        .filter(|m| m.starts_with("probe stop") || m.starts_with("hello disabled"))
        .collect();
    assert_eq!(stops.len(), 2);
    assert!(stops[0].starts_with("probe stop: ticks="), "{stops:?}");
    assert!(stops[1].starts_with("hello disabled after "), "{stops:?}");
}

/// A log off back to character selection: the stay lasts until the server
/// confirms it, so a line spoken while the log off is under way is heard
/// first; then the plugins hear the leaving with the character and its
/// world still readable, before the owners forget them (OpenAC
/// `CompleteCharacterLogOff` announces the leave before the teardown).
/// Stopping at selection after it is no
/// second leaving.
#[test]
fn a_log_off_to_selection_is_heard_at_the_confirmation_before_the_teardown() {
    let root = fresh_root("runner-logoff");
    install(&root, "hello");
    install(&root, "probe");

    let (address, server) = fake_server_saying("ping hello before the confirmation");
    let handle = spawn(plugin_config(address, &root)).unwrap();
    let commands = handle.commands();
    let mut seen = Seen::default();
    until(&handle, &mut seen, |s| {
        s.logs
            .iter()
            .any(|(_, m)| m.starts_with("probe event: login complete"))
    });
    commands
        .send(SessionCommand::Lifecycle(LifecycleCommand::LogOff))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.logs
            .iter()
            .any(|(_, m)| m.starts_with("probe event: logoff"))
    });
    commands.send(SessionCommand::Shutdown).unwrap();
    let summary = loop {
        let event = handle
            .next_event(Duration::from_secs(60))
            .expect("the session ends");
        if let Some(summary) = seen.take(event) {
            break summary;
        }
    };
    server.join().unwrap();
    handle.join();
    let _ = std::fs::remove_dir_all(&root);
    assert_eq!(summary.error, None);
    // The line was handed out (and so queued for the plugins) before the
    // leaving was, and hello heard it: the stay was still on.
    let spoken = seen
        .order
        .iter()
        .position(|m| m.starts_with("Bystander says") && m.contains("before the confirmation"))
        .unwrap_or_else(|| panic!("the line was spoken: {:?}", seen.order));
    let left = seen
        .order
        .iter()
        .position(|m| m.starts_with("probe event: logoff"))
        .unwrap();
    assert!(spoken < left, "{:?}", seen.order);
    assert!(
        seen.order
            .iter()
            .any(|m| m.starts_with("hello heard #1:") && m.contains("before the confirmation")),
        "{:?}",
        seen.order
    );
    assert_eq!(
        seen.order[left],
        "probe event: logoff: character=+Plugged objects=1 owned=0"
    );
    assert_eq!(
        seen.order
            .iter()
            .filter(|m| m.starts_with("probe event: logoff"))
            .count(),
        1
    );
}

/// Plugin UI: `uidemo`'s window lives in the session from its start; a
/// window attaching mid-run gets it whole (the plugin UI snapshot), the
/// player's click comes back as a command and reaches the plugin, and the
/// plugin's new values follow to the window.
#[test]
fn a_window_attaching_mid_run_shows_the_plugins_panel() {
    use ac_plugin_ui::{ActionValue, PluginUiView, UiChange, UiInput, UiValue};
    let root = fresh_root("runner-uidemo");
    install(&root, "uidemo");
    let (address, server) = fake_server();
    let handle = spawn(plugin_config(address, &root)).unwrap();
    let commands = handle.commands();
    let mut seen = Seen::default();
    until(&handle, &mut seen, |s| {
        s.logs
            .iter()
            .any(|(_, m)| m.starts_with("uidemo in-world: panel shown=true"))
    });

    // Attach mid-run: the snapshot carries the window, the canvas, the
    // image and the label over the character.
    let (to_window, window) = mpsc::channel();
    commands.send(SessionCommand::Attach(to_window)).unwrap();
    let mut view = PluginUiView::new();
    let deadline = Instant::now() + Duration::from_secs(10);
    while view.panels.is_empty() {
        assert!(Instant::now() < deadline, "no plugin UI snapshot");
        if let Ok(PresentationEvent::Interface(InterfaceEvent::PluginUi(changes))) =
            window.recv_timeout(Duration::from_millis(100))
        {
            assert_eq!(changes.first(), Some(&UiChange::Clear), "a snapshot first");
            view.apply_all(changes);
        }
    }
    let panel = view.panels[0].clone();
    assert_eq!(panel.descriptor.title, "UI Demo");
    assert_eq!(panel.window_name(), "plugin:openac.uidemo:main");
    assert!(panel.shown());
    assert_eq!(view.canvases.len(), 1);
    assert_eq!(view.images.len(), 1);
    assert_eq!(view.labels.len(), 1);
    assert_eq!(view.labels[0].object_id, PLAYER);

    // The player clicks Ping: the plugin hears it, and its new status
    // reaches the window.
    commands
        .send(SessionCommand::PluginUi(UiInput::Action {
            panel: panel.id,
            handler: "Ping".into(),
            value: ActionValue::None,
        }))
        .unwrap();
    until(&handle, &mut seen, |s| {
        s.logs
            .iter()
            .any(|(_, m)| m.starts_with("uidemo ui Ping: pings=1"))
    });
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        let status = view.panels[0].values.get("Status").cloned();
        if matches!(&status, Some(UiValue::Text(t)) if t.ends_with("pings 1")) {
            break;
        }
        assert!(
            Instant::now() < deadline,
            "the window never saw the new status: {status:?}"
        );
        if let Ok(PresentationEvent::Interface(InterfaceEvent::PluginUi(changes))) =
            window.recv_timeout(Duration::from_millis(100))
        {
            view.apply_all(changes);
        }
    }

    commands.send(SessionCommand::Detach).unwrap();
    commands.send(SessionCommand::Shutdown).unwrap();
    let summary = loop {
        let event = handle
            .next_event(Duration::from_secs(60))
            .expect("the session ends");
        if let Some(summary) = seen.take(event) {
            break summary;
        }
    };
    server.join().unwrap();
    handle.join();
    let _ = std::fs::remove_dir_all(&root);
    assert_eq!(summary.error, None);
    assert!(
        seen.logs
            .iter()
            .any(|(_, m)| m.starts_with("uidemo stop: ticks="))
    );
}
