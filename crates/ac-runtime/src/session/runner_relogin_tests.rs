//! A whole session against a loopback stand-in for a server that falls
//! silent once the character is in the world (as in OpenAC): with no
//! window attached the session logs in
//! again after the interval, and its plugins hear the logoff and then the
//! login; with a window attached it ends; a boot ends it whatever; a failed
//! attempt waits another interval, and an account with no character to
//! enter ends the recovery. The silence and the interval are shortened.

use std::net::{SocketAddr, UdpSocket};

use ac_bits::Writer;
use ac_net::crypto::Isaac;
use ac_net::packet::{PacketFlags, PacketHeader, encode, parse_packet, write_single_fragment};
use ac_plugin::fixtures::{fresh_root, install};
use ac_plugin::{HostVersion, PluginEngine, PluginSessionOptions};

use super::test_server::{bind_pair, is_login_request, recv_from_client, recv_until};
use super::*;
use crate::plugins::{PluginEvent, PluginSetup};
use crate::session::PortalWait;

const SERVER_SEED: u32 = 0x0BAD_F00D;
const CLIENT_SEED: u32 = 0x0D15_EA5E;
const PLAYER: u32 = 0x5000_000C;
const NAME: &str = "+Relogged";

/// The server is lost after 1.5 s of silence; each attempt to log in again
/// waits 0.7 s.
const TIMING: Timing = Timing {
    server_silence: Duration::from_millis(1500),
    relogin_interval: Duration::from_millis(700),
    portal_space: PORTAL_SPACE_LIMIT,
};

/// Portal space may last 1 s; the server's silence (20 s) is not what ends
/// these sessions.
const PORTAL_TIMING: Timing = Timing {
    server_silence: Duration::from_secs(20),
    relogin_interval: Duration::from_millis(700),
    portal_space: Duration::from_secs(1),
};

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

fn character_list(name: &str) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF658);
    w.u32(0);
    w.u32(1);
    w.u32(PLAYER);
    w.string16l(name).unwrap();
    w.u32(0);
    w.u32(0);
    w.i32(11);
    w.string16l("testaccount2").unwrap();
    w.u32(1);
    w.u32(1);
    w.into_vec()
}

fn words(words: &[u32]) -> Vec<u8> {
    words.iter().flat_map(|w| w.to_le_bytes()).collect()
}

/// Whether a client datagram carries a message with `opcode`.
fn carries(datagram: &[u8], opcode: u32) -> bool {
    parse_packet(datagram).is_ok_and(|packet| {
        packet
            .fragments()
            .any(|f| f.header.index == 0 && f.payload.get(..4) == Some(&opcode.to_le_bytes()))
    })
}

/// The stand-in server's two ports, bound and read as
/// [`super::test_server`] says.
struct Ports {
    login: UdpSocket,
    connect: UdpSocket,
}

impl Ports {
    fn new() -> (Self, SocketAddr) {
        let (login, connect, address) = bind_pair();
        (Self { login, connect }, address)
    }

    /// Read the login port until `until` passes, noting when a login
    /// request arrived (anything else there, such as the last connection's
    /// disconnect or its packets to a server that no longer answers, is
    /// not one) and whether a packet from `gone` (the last connection's
    /// client) carried a log off.
    fn listen(
        &self,
        until: Instant,
        gone: Option<SocketAddr>,
    ) -> (Option<(SocketAddr, Instant)>, bool) {
        let mut buf = [0u8; 2048];
        let mut logoff = false;
        while let Some((len, from)) = recv_until(&self.login, &mut buf, until) {
            if is_login_request(&buf[..len]) {
                return (Some((from, Instant::now())), logoff);
            }
            logoff |= Some(from) == gone && carries(&buf[..len], 0xF653);
        }
        (None, logoff)
    }

    /// A login request within 20 s, answered.
    fn accept(&self) -> (Connected<'_>, Instant) {
        let deadline = Instant::now() + Duration::from_secs(20);
        let (Some((client, at)), _) = self.listen(deadline, None) else {
            panic!("the client never logged in");
        };
        (self.answer(client), at)
    }

    /// Answer `client`'s login request with the connect request and take
    /// the connect response.
    fn answer(&self, client: SocketAddr) -> Connected<'_> {
        let mut buf = [0u8; 2048];
        self.login.send_to(&connect_request(), client).unwrap();
        let within = Instant::now() + Duration::from_secs(5);
        recv_from_client(&self.connect, &mut buf, client, within).expect("the connect response");
        Connected {
            socket: &self.login,
            client,
            keystream: Isaac::new(SERVER_SEED),
            sequence: 2,
            fragment_sequence: 1,
        }
    }
}

/// One accepted connection: sealed, sequenced server packets, one message
/// each.
struct Connected<'a> {
    socket: &'a UdpSocket,
    client: SocketAddr,
    keystream: Isaac,
    sequence: u32,
    fragment_sequence: u32,
}

impl Connected<'_> {
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

    /// Read client packets until one carries a message with `opcode`.
    fn wait_for(&self, opcode: u32) {
        let deadline = Instant::now() + Duration::from_secs(20);
        let mut buf = [0u8; 2048];
        while let Some(len) = recv_from_client(self.socket, &mut buf, self.client, deadline) {
            if carries(&buf[..len], opcode) {
                return;
            }
        }
        panic!("the client never sent {opcode:#06x}");
    }

    /// The data check, the character list (with `name` on it) and the
    /// server's word that it is ready.
    fn send_selection(&mut self, name: &str) {
        self.send(&words(&[0xF7E5]));
        self.send(&character_list(name));
        self.send(&words(&[0xF7EA]));
    }

    /// Into the world as +Relogged, up to the character's own object (the
    /// client says its login is complete, and the plugins hear it); then
    /// nothing more. Returns when the last packet went out.
    fn enter_world(&mut self) -> Instant {
        self.send_selection(NAME);
        self.wait_for(0xF7C8);
        self.send(&words(&[0xF7DF]));
        self.wait_for(0xF657);
        self.send(&words(&[0xF745, PLAYER, 0x11, 0, 0, 0, 0, 0, 0, 0]));
        Instant::now()
    }

    /// Into the world as +Relogged, but the character's own object never
    /// comes: the client stays in portal space.
    fn enter_world_without_the_character(&mut self) {
        self.send_selection(NAME);
        self.wait_for(0xF7C8);
        self.send(&words(&[0xF7DF]));
        self.wait_for(0xF657);
    }
}

fn config(address: SocketAddr, plugins: Option<PluginSetup>) -> SessionConfig {
    SessionConfig {
        server: address,
        account: "testaccount2".into(),
        password: "x".into(),
        character: CharacterSelector::Name(NAME.into()),
        loss: LossOptions::default(),
        enter_world: true,
        await_character_selection: false,
        data_directory: None,
        tables: None,
        content: None,
        diagnostics: NetDiagnostics::default(),
        login: LoginAutomation::default(),
        plugins,
    }
}

fn probe_plugins(root: &std::path::Path) -> PluginSetup {
    PluginSetup {
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
    }
}

/// What the test reads off the session's events, in order.
#[derive(Default)]
struct Seen {
    relogins: Vec<Relogin>,
    failures: Vec<SessionError>,
    entered: usize,
    /// The probe plugin's login and logoff lines.
    plugin_events: Vec<String>,
    /// The lifecycle states reported.
    lifecycle: Vec<LifecycleState>,
    /// Login completes sent.
    login_completes: usize,
    /// What the session knew when portal space ran out.
    portal_failures: Vec<PortalFailure>,
}

impl Seen {
    fn take(&mut self, event: HostEvent) -> Option<SessionSummary> {
        match event {
            HostEvent::Relogin(relogin) => self.relogins.push(relogin),
            HostEvent::Session(SessionEvent::Failed(error)) => self.failures.push(error),
            HostEvent::Session(SessionEvent::EnteredWorld(character)) => {
                assert_eq!(character.name, NAME);
                self.entered += 1;
            }
            HostEvent::Lifecycle(lifecycle) => self.lifecycle.push(lifecycle.state),
            HostEvent::Session(SessionEvent::LoginCompleteSent) => self.login_completes += 1,
            HostEvent::PortalFailure(failure) => self.portal_failures.push(*failure),
            HostEvent::Plugin(PluginEvent::Log { message, .. })
                if message.starts_with("probe event: login complete")
                    || message.starts_with("probe event: logoff") =>
            {
                self.plugin_events.push(message);
            }
            HostEvent::Ended(summary) => return Some(summary),
            _ => {}
        }
        None
    }

    /// Read events until `done` holds; the session must not end first.
    fn until(&mut self, handle: &SessionHandle, done: impl Fn(&Self) -> bool) {
        let deadline = Instant::now() + Duration::from_secs(30);
        while !done(self) {
            assert!(Instant::now() < deadline, "timed out");
            if let Some(event) = handle.next_event(Duration::from_millis(50))
                && let Some(summary) = self.take(event)
            {
                panic!("the session ended early: {:?}", summary.error);
            }
        }
    }

    /// Read events until the session ends, within 30 s (a session that
    /// keeps logging in again never would).
    fn end(&mut self, handle: &SessionHandle) -> SessionSummary {
        let deadline = Instant::now() + Duration::from_secs(30);
        loop {
            assert!(
                Instant::now() < deadline,
                "the session never ended: {:?}",
                self.relogins
            );
            if let Some(event) = handle.next_event(Duration::from_millis(50))
                && let Some(summary) = self.take(event)
            {
                return summary;
            }
        }
    }
}

#[test]
fn a_windowless_session_logs_in_again_after_the_interval_and_its_plugins_hear_it() {
    let root = fresh_root("runner-relogin");
    install(&root, "probe");
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        let silent_from = first.enter_world();
        // Silence. The client finds the server gone and logs in again on a
        // new connection, never sending a log off to the gone one.
        let (second, logoff) =
            ports.listen(Instant::now() + Duration::from_secs(20), Some(first.client));
        let (client, second_login) = second.expect("the client logged in again");
        assert!(!logoff, "no log off went to the gone server");
        let mut again = ports.answer(client);
        again.enter_world();
        again.wait_for(0xF653);
        again.send(&words(&[0xF653]));
        second_login.saturating_duration_since(silent_from)
    });

    let handle = spawn_timed(config(address, Some(probe_plugins(&root))), TIMING).unwrap();
    let mut seen = Seen::default();
    seen.until(&handle, |s| {
        s.plugin_events
            .iter()
            .filter(|e| e.starts_with("probe event: login complete"))
            .count()
            == 2
    });
    handle.commands().send(SessionCommand::Shutdown).unwrap();
    let summary = seen.end(&handle);
    let waited = server.join().unwrap();
    handle.join();
    let _ = std::fs::remove_dir_all(&root);

    assert_eq!(summary.error, None, "a re-login is not a failure");
    assert_eq!(summary.connections, 2);
    assert_eq!(seen.entered, 2);
    assert_eq!(seen.failures, [SessionError::ConnectionLost]);
    assert_eq!(
        seen.relogins,
        [Relogin::Scheduled {
            after: TIMING.relogin_interval
        }]
    );
    // Not before the server was found gone and the interval passed.
    assert!(
        waited >= TIMING.server_silence + TIMING.relogin_interval,
        "logged in again {waited:?} after the silence began"
    );
    // The plugins heard the stay end with the connection, then the new one
    // begin (OpenAC: the leave is announced as the lost session ends; the
    // next login is a login like the first).
    let character = format!("character={NAME} objects=1 owned=0");
    assert_eq!(
        seen.plugin_events,
        [
            format!("probe event: login complete: {character}"),
            format!("probe event: logoff: {character}"),
            format!("probe event: login complete: {character}"),
            format!("probe event: logoff: {character}"),
        ]
    );
    // The lost connection stopped the session before it started again.
    let stopped_then_started = seen
        .lifecycle
        .windows(2)
        .any(|w| w == [LifecycleState::Stopped, LifecycleState::Starting]);
    assert!(stopped_then_started, "{:?}", seen.lifecycle);
}

#[test]
fn a_session_with_a_window_attached_ends_when_its_server_falls_silent() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.enter_world();
        // Long enough for a re-login to have come.
        let wait = TIMING.server_silence + TIMING.relogin_interval + Duration::from_secs(2);
        let (again, _) = ports.listen(Instant::now() + wait, None);
        again.is_none()
    });

    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    let mut seen = Seen::default();
    seen.until(&handle, |s| s.entered == 1);
    let (to_window, _window) = mpsc::channel();
    handle
        .commands()
        .send(SessionCommand::Attach(to_window))
        .unwrap();
    let summary = seen.end(&handle);
    let no_second_login = server.join().unwrap();
    handle.join();

    assert_eq!(
        summary.error.as_deref(),
        Some("the connection to the server was lost")
    );
    assert_eq!(summary.connections, 1);
    assert!(seen.relogins.is_empty(), "{:?}", seen.relogins);
    assert!(no_second_login, "the session did not log in again");
}

/// A remote presentation is not a window the player sits at: the session
/// logs in again as it does with nothing attached, and the remote stays
/// attached through it, shown the new connection from a fresh snapshot.
#[test]
fn a_session_with_a_remote_attached_logs_in_again_and_keeps_the_remote() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.enter_world();
        // Silence: the client finds the server gone and logs in again.
        let (second, _) =
            ports.listen(Instant::now() + Duration::from_secs(20), Some(first.client));
        let (client, _) = second.expect("the client logged in again");
        let mut again = ports.answer(client);
        again.enter_world();
        again.wait_for(0xF653);
        again.send(&words(&[0xF653]));
    });

    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    let mut seen = Seen::default();
    seen.until(&handle, |s| s.entered == 1);
    let (to_remote, remote) = mpsc::channel();
    handle
        .commands()
        .send(SessionCommand::AttachRemote(to_remote))
        .unwrap();
    seen.until(&handle, |s| s.entered == 2);
    let snapshots = remote
        .try_iter()
        .filter(|event| matches!(event, PresentationEvent::Snapshot(_)))
        .count();
    handle.commands().send(SessionCommand::Shutdown).unwrap();
    let summary = seen.end(&handle);
    server.join().unwrap();
    handle.join();

    assert_eq!(summary.error, None, "a re-login is not a failure");
    assert_eq!(summary.connections, 2);
    assert_eq!(
        seen.relogins,
        [Relogin::Scheduled {
            after: TIMING.relogin_interval
        }]
    );
    assert!(
        snapshots >= 2,
        "the remote saw {snapshots} snapshots: the attach's and the new connection's"
    );
}

/// A session told to keep the world's messages hands a listener the world
/// as the server described it: the player's own creation, byte for byte.
#[test]
fn a_listener_of_the_world_tap_is_given_the_messages_that_made_the_world() {
    use super::super::world_tap::WorldTapCommand;
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.enter_world();
        first.wait_for(0xF653);
        first.send(&words(&[0xF653]));
    });
    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    // Before the login, as a host that may be viewed does.
    handle
        .commands()
        .send(SessionCommand::WorldTap(WorldTapCommand::Keep(true)))
        .unwrap();
    let mut seen = Seen::default();
    seen.until(&handle, |s| s.entered == 1);
    let created = words(&[0xF745, PLAYER, 0x11, 0, 0, 0, 0, 0, 0, 0]);
    // The creation follows the entry; ask until the tap has it.
    let deadline = Instant::now() + Duration::from_secs(10);
    let snapshot = loop {
        assert!(Instant::now() < deadline, "the tap never had the player");
        let (to_listener, listener) = mpsc::channel();
        handle
            .commands()
            .send(SessionCommand::WorldTap(WorldTapCommand::Listen(Some(
                to_listener,
            ))))
            .unwrap();
        let batch = listener
            .recv_timeout(Duration::from_secs(5))
            .expect("the world as it stands");
        if !batch.messages.is_empty() {
            break batch;
        }
        thread::sleep(Duration::from_millis(20));
    };
    handle.commands().send(SessionCommand::Shutdown).unwrap();
    seen.end(&handle);
    server.join().unwrap();
    handle.join();

    assert_eq!(snapshot.player, PLAYER);
    assert_eq!(snapshot.messages.len(), 1, "{:?}", snapshot.messages);
    assert_eq!(snapshot.messages[0].body, created);
    assert!(!snapshot.messages[0].client_to_server);
}

#[test]
fn a_boot_ends_a_windowless_session_without_logging_in_again() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.enter_world();
        // The account logged in elsewhere: the character error, then
        // silence.
        first.send(&words(&[0xF659, 0x01]));
        let wait = TIMING.server_silence + TIMING.relogin_interval + Duration::from_secs(2);
        let (again, _) = ports.listen(Instant::now() + wait, None);
        again.is_none()
    });

    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    let mut seen = Seen::default();
    let summary = seen.end(&handle);
    let no_second_login = server.join().unwrap();
    let progress = handle.progress();
    handle.join();

    assert_eq!(seen.entered, 1);
    assert!(progress > 0, "the session loop counts its passes");
    // The launcher shows the server's words; booted out of the world, the
    // login is not one still held (no quick retry).
    assert_eq!(
        summary.server_refusal,
        Some(super::ServerRefusal {
            code: Some(0x01),
            message: "Another account is already logged on from this client.".into(),
            still_logged_on: false,
        })
    );
    assert!(
        summary
            .error
            .as_deref()
            .is_some_and(|e| e.starts_with("the server ended the session")),
        "{:?}",
        summary.error
    );
    assert_eq!(summary.connections, 1);
    assert!(seen.relogins.is_empty(), "{:?}", seen.relogins);
    assert!(no_second_login, "the session did not log in again");
}

// Not an OpenAC test: the server refusing the login itself (an earlier
// session it has not let go of) says the login is still held, so a
// launcher may try again once the server drops it.
#[test]
fn a_refused_login_says_the_account_is_still_logged_on() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.send(&words(&[0xF659, 0x01]));
    });

    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    let mut seen = Seen::default();
    let summary = seen.end(&handle);
    server.join().unwrap();
    handle.join();

    assert_eq!(seen.entered, 0);
    assert_eq!(
        summary.server_refusal,
        Some(super::ServerRefusal {
            code: Some(0x01),
            message: "Another account is already logged on from this client.".into(),
            still_logged_on: true,
        })
    );
}

#[test]
fn a_failed_attempt_waits_another_interval_and_a_missing_character_ends_the_recovery() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.enter_world();
        // The first attempt finds the account still in use.
        let (mut second, _) = ports.accept();
        second.send(&words(&[0xF659, 0x08]));
        let refused_at = Instant::now();
        // The next one comes an interval later and finds no +Relogged.
        let (mut third, third_at) = ports.accept();
        third.send_selection("+Someone");
        third_at.saturating_duration_since(refused_at)
    });

    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    let mut seen = Seen::default();
    let summary = seen.end(&handle);
    let between = server.join().unwrap();
    handle.join();

    assert_eq!(seen.entered, 1);
    assert_eq!(summary.connections, 3);
    assert!(
        summary
            .error
            .as_deref()
            .is_some_and(|e| e.starts_with("no available character matches")),
        "{:?}",
        summary.error
    );
    assert_eq!(seen.relogins.len(), 2, "{:?}", seen.relogins);
    assert_eq!(
        seen.relogins[0],
        Relogin::Scheduled {
            after: TIMING.relogin_interval
        }
    );
    assert!(
        matches!(
            &seen.relogins[1],
            Relogin::AttemptFailed { error, after }
                if error.starts_with("the server ended the session")
                    && *after == TIMING.relogin_interval
        ),
        "{:?}",
        seen.relogins[1]
    );
    assert!(
        between >= TIMING.relogin_interval,
        "the next attempt came {between:?} after the refusal"
    );
}

/// The server's answer to a wrong password: an account boot (0xF7DC) with
/// its reason.
fn wrong_password_boot() -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF7DC);
    w.string16l(" because the password entered for this account was not correct")
        .unwrap();
    w.into_vec()
}

const WRONG_PASSWORD_TEXT: &str = "You have been booted from Asheron's Call because the password \
                                   entered for this account was not correct.";

// Not an OpenAC test: a login the server boots (a wrong password) ends at
// once with the game's sentence, not at the connect timeout, and the
// launcher hears the server's words.
#[test]
fn a_booted_login_ends_at_once_with_the_games_text() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.send(&wrong_password_boot());
        Instant::now()
    });

    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    let mut seen = Seen::default();
    let summary = seen.end(&handle);
    let ended = Instant::now();
    let booted_at = server.join().unwrap();
    handle.join();

    assert!(
        ended.saturating_duration_since(booted_at) < crate::session::CONNECT_TIMEOUT / 2,
        "the session ended {:?} after the boot",
        ended.saturating_duration_since(booted_at)
    );
    assert_eq!(summary.error.as_deref(), Some(WRONG_PASSWORD_TEXT));
    assert_eq!(
        summary.server_refusal,
        Some(super::ServerRefusal {
            code: None,
            message: WRONG_PASSWORD_TEXT.into(),
            still_logged_on: false,
        })
    );
    assert_eq!(summary.connections, 1);
    assert_eq!(
        seen.failures,
        [SessionError::AccountBooted(WRONG_PASSWORD_TEXT.into())]
    );
}

// A login again after a lost server that the server boots (the password
// changed meanwhile) ends the recovery: the same password would only be
// refused again every interval.
#[test]
fn a_boot_of_an_attempt_to_log_in_again_ends_the_recovery() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.enter_world();
        let (mut second, _) = ports.accept();
        second.send(&wrong_password_boot());
        // Long enough for a further attempt to have come.
        let wait = TIMING.relogin_interval * 3;
        let (again, _) = ports.listen(Instant::now() + wait, None);
        again.is_none()
    });

    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    let mut seen = Seen::default();
    let summary = seen.end(&handle);
    let no_third_login = server.join().unwrap();
    handle.join();

    assert_eq!(seen.entered, 1);
    assert_eq!(summary.connections, 2);
    assert_eq!(summary.error.as_deref(), Some(WRONG_PASSWORD_TEXT));
    assert_eq!(
        seen.relogins,
        [Relogin::Scheduled {
            after: TIMING.relogin_interval
        }]
    );
    assert!(no_third_login, "the session did not try again");
}

/// OpenAC `WorldSession.BeginConnect` and `ConnectionUiController`: a
/// window attached while the server has not answered the login request
/// hears that the session is connecting, and the connecting screen's
/// Cancel (a shutdown) ends the attempt at once, not at the connect
/// timeout.
#[test]
fn a_window_sees_the_connecting_screen_and_its_cancel_ends_the_attempt() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        // The login request arrives and is never answered.
        let deadline = Instant::now() + Duration::from_secs(20);
        ports.listen(deadline, None).0.is_some()
    });
    let handle = spawn_timed(config(address, None), TIMING).unwrap();
    let (to_window, window) = mpsc::channel();
    handle
        .commands()
        .send(SessionCommand::Attach(to_window))
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    let connecting = loop {
        assert!(Instant::now() < deadline, "no connecting screen");
        match window.recv_timeout(Duration::from_millis(50)) {
            Ok(PresentationEvent::PreGame(PreGameEvent::Connection(view))) => break view,
            Ok(_) | Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => panic!("the session dropped the window"),
        }
    };
    assert_eq!(
        connecting.status,
        super::super::pregame::ConnectionStatus::Connecting
    );
    assert_eq!(connecting.connection_progress, 0.0);
    let asked = Instant::now();
    handle.commands().send(SessionCommand::Shutdown).unwrap();
    let mut seen = Seen::default();
    let summary = seen.end(&handle);
    let ended_after = asked.elapsed();
    assert!(server.join().unwrap(), "the login request went out");
    handle.join();
    assert!(summary.error.is_none(), "{:?}", summary.error);
    assert!(
        ended_after < LOGIN_TIMEOUT / 2,
        "the cancel took {ended_after:?}"
    );
}

// This client's own rule (the game waits in portal space without a limit):
// a session whose character never arrives ends its connection once portal
// space has lasted longer than the limit, after telling what it knew. With
// no window attached it then logs in again, as after a lost server.
#[test]
fn a_windowless_session_stuck_in_portal_space_tells_why_and_logs_in_again() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        // Portal space begins after this: the character is not yet chosen.
        let before_entry = Instant::now();
        first.enter_world_without_the_character();
        let (second, logoff) =
            ports.listen(Instant::now() + Duration::from_secs(20), Some(first.client));
        let (client, second_login) = second.expect("the client logged in again");
        assert!(!logoff, "no log off went to the connection given up");
        let mut again = ports.answer(client);
        again.enter_world();
        again.wait_for(0xF653);
        again.send(&words(&[0xF653]));
        second_login.saturating_duration_since(before_entry)
    });

    let handle = spawn_timed(config(address, None), PORTAL_TIMING).unwrap();
    let mut seen = Seen::default();
    seen.until(&handle, |s| s.login_completes == 1);
    handle.commands().send(SessionCommand::Shutdown).unwrap();
    let summary = seen.end(&handle);
    let waited = server.join().unwrap();
    handle.join();

    assert_eq!(summary.error, None, "a re-login is not a failure");
    assert_eq!(summary.connections, 2);
    assert_eq!(seen.entered, 2);
    assert_eq!(
        seen.failures,
        [SessionError::PortalSpaceTimeout { limit_seconds: 1 }]
    );
    assert_eq!(
        seen.relogins,
        [Relogin::Scheduled {
            after: PORTAL_TIMING.relogin_interval
        }]
    );
    assert!(
        waited >= PORTAL_TIMING.portal_space + PORTAL_TIMING.relogin_interval,
        "logged in again {waited:?} after the connection was accepted"
    );
    // The record: which wait, how long, and what was missing.
    let [failure] = &seen.portal_failures[..] else {
        panic!("one record: {:?}", seen.portal_failures);
    };
    assert_eq!(failure.wait, PortalWait::Login);
    assert_eq!(failure.limit, PORTAL_TIMING.portal_space);
    assert!(failure.waited > failure.limit, "{:?}", failure.waited);
    assert_eq!(failure.phase, Phase::InWorld);
    assert!(!failure.login_complete_sent);
    assert!(!failure.world.player_object, "the character never came");
    assert!(!failure.world.player_described);
    assert!(!failure.world.window_attached);
    assert!(failure.link.stats.packets_received > 0);
    // The last messages show where the login stopped: the server's word
    // that it is ready came, the character's object did not.
    let opcodes: Vec<u32> = failure.recent.iter().map(|note| note.opcode).collect();
    assert_eq!(opcodes.last(), Some(&0xF7DF), "{opcodes:#06x?}");
    assert!(!opcodes.contains(&0xF745), "{opcodes:#06x?}");
    assert!(failure.recent.iter().all(|note| note.bytes >= 4));
}

// With a window attached the session ends (its window shows the end, and a
// launcher starts the client again).
#[test]
fn a_session_with_a_window_attached_ends_when_portal_space_does_not() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.enter_world_without_the_character();
        // Long enough for a re-login to have come.
        let wait =
            PORTAL_TIMING.portal_space + PORTAL_TIMING.relogin_interval + Duration::from_secs(2);
        let (again, _) = ports.listen(Instant::now() + wait, None);
        again.is_none()
    });

    let handle = spawn_timed(config(address, None), PORTAL_TIMING).unwrap();
    let mut seen = Seen::default();
    seen.until(&handle, |s| s.entered == 1);
    let (to_window, _window) = mpsc::channel();
    handle
        .commands()
        .send(SessionCommand::Attach(to_window))
        .unwrap();
    let summary = seen.end(&handle);
    let no_second_login = server.join().unwrap();
    handle.join();

    assert_eq!(
        summary.error.as_deref(),
        Some("portal space did not end within 1 s")
    );
    assert_eq!(summary.connections, 1);
    assert!(seen.relogins.is_empty(), "{:?}", seen.relogins);
    assert!(no_second_login, "the session did not log in again");
    let [failure] = &seen.portal_failures[..] else {
        panic!("one record: {:?}", seen.portal_failures);
    };
    assert_eq!(failure.wait, PortalWait::Login);
    assert!(failure.world.window_attached);
}

// A login that completes in time is left alone, however short the limit
// was while it ran: the watch starts over once portal space ends.
#[test]
fn a_login_that_completes_is_not_ended_by_the_portal_space_limit() {
    let (ports, address) = Ports::new();
    let server = thread::spawn(move || {
        let (mut first, _) = ports.accept();
        first.enter_world();
        first.wait_for(0xF653);
        first.send(&words(&[0xF653]));
    });

    let handle = spawn_timed(config(address, None), PORTAL_TIMING).unwrap();
    let mut seen = Seen::default();
    seen.until(&handle, |s| s.login_completes == 1);
    // In the world for longer than the limit.
    let stay = Instant::now() + PORTAL_TIMING.portal_space + Duration::from_millis(500);
    while Instant::now() < stay {
        if let Some(event) = handle.next_event(Duration::from_millis(50)) {
            assert!(seen.take(event).is_none(), "the session ended");
        }
    }
    handle.commands().send(SessionCommand::Shutdown).unwrap();
    let summary = seen.end(&handle);
    server.join().unwrap();
    handle.join();

    assert_eq!(summary.error, None);
    assert_eq!(summary.connections, 1);
    assert!(
        seen.portal_failures.is_empty(),
        "{:?}",
        seen.portal_failures
    );
}
