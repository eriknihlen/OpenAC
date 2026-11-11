//! A session's remote endpoint end to end: a relay on one side (the test
//! relay), the session on the other (the test, holding the channels a
//! session holds). The hard rule under test: whatever the relay or the
//! browser does, the session is asked for nothing worse than a detach.

mod support;

use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::time::{Duration, Instant};

use ac_remote::endpoint::{Codec, INPUT_SILENCE};
use ac_remote::protocol::{SessionState, Status};
use ac_remote::remote::Remote;
use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_runtime::session::{Generation, PresentationEvent, SessionCommand};
use support::{PATIENCE, RelayConn, TestRelay, member};

/// A frame of `E` bytes, one per event; any input frame is "hold forward",
/// stamped, unless it starts with 0xFF, which is no command.
#[derive(Debug)]
struct TestCodec;

impl Codec for TestCodec {
    fn encode(&mut self, events: &[PresentationEvent]) -> Result<Vec<u8>, String> {
        Ok(vec![b'E'; events.len()])
    }

    fn decode(&mut self, frame: &[u8]) -> Result<SessionCommand, String> {
        if frame.first() == Some(&0xFF) {
            return Err("not a command".to_owned());
        }
        let keys = MovementKeys {
            forward: true,
            ..MovementKeys::default()
        };
        Ok(SessionCommand::AtGeneration {
            generation: Generation::default(),
            command: Box::new(SessionCommand::Movement(MovementCommand::Keys(keys))),
        })
    }
}

const ATTACH: &str = r#"{"type":"attach","request_id":7,
    "subscription":{"motions":false,"sound":false,"effects":false,"navigation":false,"objects":"all"},
    "permissions":{"control":true,"lifecycle":["log_off","reconnect"]}}"#;

/// The session's side: what it was sent, and where its events go.
struct Session {
    commands: Receiver<SessionCommand>,
}

impl Session {
    fn next(&self) -> SessionCommand {
        self.commands
            .recv_timeout(PATIENCE)
            .expect("a command for the session")
    }

    /// Nothing reaches the session within `wait`.
    fn quiet_for(&self, wait: Duration) {
        match self.commands.recv_timeout(wait) {
            Err(RecvTimeoutError::Timeout) => {}
            other => panic!("the session was sent {other:?}"),
        }
    }

    fn expect_attach(&self) -> Sender<PresentationEvent> {
        match self.next() {
            SessionCommand::AttachRemote(to) => to,
            other => panic!("expected a remote attach, got {other:?}"),
        }
    }

    fn expect_detach(&self) {
        match self.next() {
            SessionCommand::Detach => {}
            other => panic!("expected a detach, got {other:?}"),
        }
    }
}

/// A remote on a fresh relay, its relay connection past the hello.
fn start() -> (TestRelay, Remote, Session, RelayConn) {
    let relay = TestRelay::start();
    let (to_session, commands) = mpsc::channel();
    let remote = Remote::start(relay.config(), TestCodec, to_session).expect("remote");
    let conn = connect(&relay);
    (relay, remote, Session { commands }, conn)
}

fn connect(relay: &TestRelay) -> RelayConn {
    let mut conn = relay.accept();
    conn.upgrade();
    conn.read_text();
    conn.send_text(r#"{"type":"hello_ok","protocol":1,"ping_interval_ms":5000}"#);
    conn
}

/// Attached as request 7; where the session's events go.
fn attach(conn: &mut RelayConn, session: &Session) -> Sender<PresentationEvent> {
    conn.send_text(ATTACH);
    let to_remote = session.expect_attach();
    let attached = conn.read_text();
    assert_eq!(
        member(&attached, "type"),
        Some("\"attached\""),
        "{attached}"
    );
    assert_eq!(member(&attached, "request_id"), Some("7"));
    to_remote
}

fn read_binary(conn: &mut RelayConn) -> Vec<u8> {
    let deadline = Instant::now() + PATIENCE;
    loop {
        assert!(Instant::now() < deadline, "no binary frame");
        let frame = conn.read_frame().expect("a frame");
        match frame.opcode {
            2 => return frame.payload,
            10 => {}
            other => panic!("expected binary, got opcode {other}"),
        }
    }
}

fn text(line: &str) -> PresentationEvent {
    PresentationEvent::StatusText(line.to_owned())
}

#[test]
fn a_browser_attaches_gets_the_stream_and_leaves() {
    let (_relay, _remote, session, mut conn) = start();
    let to_remote = attach(&mut conn, &session);
    to_remote.send(text("one")).unwrap();
    assert_eq!(read_binary(&mut conn), b"E");
    conn.send_text(r#"{"type":"detach","request_id":7}"#);
    session.expect_detach();
    let detached = conn.read_text();
    assert_eq!(member(&detached, "type"), Some("\"detached\""));
    assert_eq!(member(&detached, "reason"), Some("\"browser\""));
    // The session's events have nowhere to go any more.
    let deadline = Instant::now() + PATIENCE;
    while to_remote.send(text("late")).is_ok() {
        assert!(Instant::now() < deadline, "the remote kept the channel");
        std::thread::sleep(Duration::from_millis(5));
    }
}

#[test]
fn a_relay_that_goes_away_detaches_and_the_endpoint_comes_back() {
    let (relay, _remote, session, mut conn) = start();
    let _to_remote = attach(&mut conn, &session);
    conn.send_text(r#"{"type":"control","request_id":7,"on":true}"#);
    drop(conn);
    // A detach, which lets go of the keys; never anything that ends the
    // session.
    session.expect_detach();
    let mut again = connect(&relay);
    session.quiet_for(Duration::from_millis(200));
    // And it can be attached again.
    attach(&mut again, &session);
}

#[test]
fn input_reaches_the_session_under_control_and_control_lapses_in_silence() {
    let (_relay, _remote, session, mut conn) = start();
    let _to_remote = attach(&mut conn, &session);
    // Not in control: dropped.
    conn.send_binary(b"keys");
    session.quiet_for(Duration::from_millis(200));
    conn.send_text(r#"{"type":"control","request_id":7,"on":true}"#);
    conn.send_binary(b"keys");
    let sent = Instant::now();
    assert!(matches!(
        session.next(),
        SessionCommand::AtGeneration { .. }
    ));
    // Silence: the keys are let go and the relay hears control is gone.
    match session.next() {
        SessionCommand::Movement(MovementCommand::ReleaseKeys) => {}
        other => panic!("expected the keys released, got {other:?}"),
    }
    assert!(sent.elapsed() >= INPUT_SILENCE, "{:?}", sent.elapsed());
    let state = conn.read_text();
    assert_eq!(member(&state, "type"), Some("\"control_state\""), "{state}");
    assert_eq!(member(&state, "controlling"), Some("false"));
    assert_eq!(member(&state, "reason"), Some("\"heartbeat\""));
}

#[test]
fn bytes_that_are_no_command_end_the_attach_and_nothing_else() {
    let (_relay, _remote, session, mut conn) = start();
    let _to_remote = attach(&mut conn, &session);
    conn.send_text(r#"{"type":"control","request_id":7,"on":true}"#);
    conn.send_binary(&[0xFF, 0x00]);
    session.expect_detach();
    let detached = conn.read_text();
    assert_eq!(
        member(&detached, "reason"),
        Some("\"protocol\""),
        "{detached}"
    );
    session.quiet_for(Duration::from_millis(200));
}

#[test]
fn a_session_whose_channel_closed_is_reported_and_not_detached_again() {
    let (_relay, _remote, session, mut conn) = start();
    let to_remote = attach(&mut conn, &session);
    // The session ended, or something else attached in this one's place.
    drop(to_remote);
    let detached = conn.read_text();
    assert_eq!(
        member(&detached, "reason"),
        Some("\"session_ended\""),
        "{detached}"
    );
    // No detach: it would take away whatever is attached now.
    session.quiet_for(Duration::from_millis(200));
}

#[test]
fn shutting_the_remote_down_detaches_and_stops_its_thread() {
    let (_relay, remote, session, mut conn) = start();
    let _to_remote = attach(&mut conn, &session);
    let asked = Instant::now();
    remote.shutdown();
    assert!(
        asked.elapsed() < Duration::from_secs(2),
        "{:?}",
        asked.elapsed()
    );
    session.expect_detach();
}

#[test]
fn what_the_session_is_doing_reaches_the_relay() {
    let (_relay, remote, _session, mut conn) = start();
    remote.status(Status {
        generation: Some(4),
        character_name: Some("Second".to_owned()),
        character_guid: Some(0x5000_0002),
        state: Some(SessionState::PortalSpace),
    });
    let status = conn.read_text();
    assert_eq!(member(&status, "type"), Some("\"status\""), "{status}");
    assert_eq!(member(&status, "generation"), Some("4"));
    assert_eq!(member(&status, "character_name"), Some("\"Second\""));
    assert_eq!(member(&status, "state"), Some("\"portal_space\""));
}
