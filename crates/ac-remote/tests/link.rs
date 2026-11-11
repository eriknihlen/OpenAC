//! The link against an in-process relay: handshake, hello, keepalive,
//! frames both ways, every kind of loss and the reconnect after it.

mod support;

use std::time::{Duration, Instant};

use ac_remote::protocol::{
    ControlReason, EndpointMessage, RefuseReason, RelayMessage, SessionState, Status,
};
use ac_remote::ws::{HandshakeError, WsError};
use ac_remote::{Inbound, Link, LinkLoss, SendError, Timing};
use support::{PATIENCE, TOKEN, TestRelay, expect_connected, member, next_event};

fn expect_failed(link: &Link) -> (LinkLoss, Duration) {
    match next_event(link) {
        Inbound::ConnectFailed { loss, retry_in } => (loss, retry_in),
        other => panic!("expected ConnectFailed, got {other:?}"),
    }
}

fn expect_lost(link: &Link) -> (LinkLoss, Duration) {
    match next_event(link) {
        Inbound::Disconnected { loss, retry_in } => (loss, retry_in),
        other => panic!("expected Disconnected, got {other:?}"),
    }
}

#[test]
fn the_upgrade_carries_the_bearer_token_and_the_hello_comes_first() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    let request = conn.upgrade();
    assert_eq!(request.line, "GET /client/ws HTTP/1.1");
    let host = format!("127.0.0.1:{}", relay.url().port);
    assert_eq!(request.header("Host"), Some(host.as_str()));
    let bearer = format!("Bearer {TOKEN}");
    assert_eq!(request.header("Authorization"), Some(bearer.as_str()));
    let hello = conn.read_text();
    assert_eq!(member(&hello, "type"), Some("\"hello\""));
    assert_eq!(member(&hello, "protocol"), Some("1"));
    assert_eq!(member(&hello, "character_name"), Some("\"First\""));
    assert!(!hello.contains(TOKEN), "{hello}");
    conn.send_text(r#"{"type":"hello_ok","protocol":1,"ping_interval_ms":5000}"#);
    assert_eq!(
        next_event(&link),
        Inbound::Connected {
            ping_interval_ms: 5000
        }
    );
}

#[test]
fn a_refused_token_is_a_typed_error_and_the_link_backs_off() {
    let relay = TestRelay::start();
    let link = relay.link();
    let refuse = || {
        let mut conn = relay.accept();
        conn.read_request();
        // A body that repeats the token must not reach any error text.
        conn.send_raw(
            format!("HTTP/1.1 401 Unauthorized\r\nContent-Length: 20\r\n\r\nBearer {TOKEN}")
                .as_bytes(),
        );
    };
    refuse();
    let (loss, first) = expect_failed(&link);
    assert_eq!(loss, LinkLoss::Handshake(HandshakeError::Status(401)));
    assert!(!format!("{loss} {loss:?}").contains(TOKEN));
    assert_eq!(first, Duration::from_millis(20));
    refuse();
    let (_, second) = expect_failed(&link);
    assert_eq!(second, Duration::from_millis(40));
    // A good connection starts the backoff over.
    let mut conn = relay.accept();
    conn.accept_session(&link);
    conn.send_close(1000);
    let (loss, retry_in) = expect_lost(&link);
    assert_eq!(loss, LinkLoss::Closed(Some(1000)));
    assert_eq!(retry_in, Duration::from_millis(20));
}

#[test]
fn refused_backs_off_and_reconnects() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.upgrade();
    conn.read_text();
    conn.send_text(r#"{"type":"refused","reason":"protocol"}"#);
    conn.send_close(1008);
    let (loss, _) = expect_failed(&link);
    assert_eq!(loss, LinkLoss::Refused(RefuseReason::Protocol));
    // The newer connection wins a duplicate; this one hears it while
    // connected.
    let mut conn = relay.accept();
    conn.accept_session(&link);
    conn.send_text(r#"{"type":"refused","reason":"duplicate"}"#);
    let (loss, _) = expect_lost(&link);
    assert_eq!(loss, LinkLoss::Refused(RefuseReason::Duplicate));
    relay.accept().accept_session(&link);
}

#[test]
fn a_ping_is_answered() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.accept_session(&link);
    conn.send_frame(true, 9, b"abc");
    let pong = conn.read_frame().expect("a pong");
    assert_eq!((pong.opcode, pong.payload), (10, b"abc".to_vec()));
}

#[test]
fn fragmented_text_is_joined_and_control_reaches_the_caller() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.accept_session(&link);
    conn.send_frame(false, 1, br#"{"type":"det"#);
    conn.send_frame(true, 9, b"p");
    conn.send_frame(true, 0, br#"ach","request_id":7,"later":[1]}"#);
    assert_eq!(
        next_event(&link),
        Inbound::Control(RelayMessage::Detach { request_id: 7 })
    );
    assert_eq!(conn.read_frame().expect("pong").opcode, 10);
    conn.send_text(r#"{"type":"weather"}"#);
    assert_eq!(
        next_event(&link),
        Inbound::Control(RelayMessage::Unknown {
            type_name: "weather".into()
        })
    );
    conn.send_text(r#"{"type":"control","request_id":7,"on":true}"#);
    assert_eq!(
        next_event(&link),
        Inbound::Control(RelayMessage::Control {
            request_id: 7,
            on: true
        })
    );
}

#[test]
fn binary_frames_go_both_ways_in_order_with_control() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.accept_session(&link);
    conn.send_binary(&[1, 2, 3]);
    assert_eq!(next_event(&link), Inbound::Binary(vec![1, 2, 3]));
    let big = vec![0x5A; 200_000];
    link.send_control(EndpointMessage::Attached { request_id: 9 })
        .unwrap();
    link.send_binary(big.clone()).unwrap();
    link.send_control(EndpointMessage::ControlState {
        request_id: 9,
        controlling: false,
        reason: ControlReason::Heartbeat,
    })
    .unwrap();
    assert_eq!(conn.read_text(), r#"{"type":"attached","request_id":9}"#);
    let frame = conn.read_frame().unwrap();
    assert_eq!((frame.opcode, frame.payload.len()), (2, big.len()));
    assert_eq!(frame.payload, big);
    assert_eq!(
        conn.read_text(),
        r#"{"type":"control_state","request_id":9,"controlling":false,"reason":"heartbeat"}"#
    );
}

#[test]
fn an_oversized_frame_drops_the_link_and_it_reconnects() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.accept_session(&link);
    // Only the header of a 64 KiB + 1 binary frame.
    conn.send_raw(&support::frame(true, 2, &vec![0; 64 * 1024 + 1])[..10]);
    let (loss, _) = expect_lost(&link);
    assert_eq!(loss, LinkLoss::Ws(WsError::TooBig { limit: 64 * 1024 }));
    // It said why: close code 1009, message too big.
    let frames = conn.read_to_end();
    assert!(
        frames
            .iter()
            .any(|f| f.opcode == 8 && f.payload.starts_with(&1009u16.to_be_bytes())),
        "{frames:?}"
    );
    relay.accept().accept_session(&link);
}

#[test]
fn garbage_drops_the_link_and_it_reconnects() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.accept_session(&link);
    conn.send_raw(&[0xFF; 32]);
    let (loss, _) = expect_lost(&link);
    assert_eq!(loss, LinkLoss::Ws(WsError::ReservedBits));
    // Garbage before the upgrade answer, too.
    let mut conn = relay.accept();
    conn.read_request();
    conn.send_raw(b"\x00\x01garbage\r\n\r\n");
    let (loss, _) = expect_failed(&link);
    assert_eq!(loss, LinkLoss::Handshake(HandshakeError::Malformed));
    // And a control message that is not JSON.
    let mut conn = relay.accept();
    conn.accept_session(&link);
    conn.send_text("not json");
    assert!(matches!(expect_lost(&link).0, LinkLoss::Message(_)));
    relay.accept().accept_session(&link);
}

#[test]
fn messages_out_of_order_before_hello_ok_are_a_loss() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.upgrade();
    conn.read_text();
    conn.send_text(r#"{"type":"detach","request_id":1}"#);
    assert!(matches!(expect_failed(&link).0, LinkLoss::Unexpected(_)));
    let mut conn = relay.accept();
    conn.upgrade();
    conn.read_text();
    conn.send_text(r#"{"type":"hello_ok","protocol":2,"ping_interval_ms":5000}"#);
    assert_eq!(expect_failed(&link).0, LinkLoss::ProtocolVersion(2));
}

#[test]
fn silence_is_a_lost_link_and_the_next_hello_has_the_new_state() {
    let relay = TestRelay::start();
    let mut config = relay.config();
    config.timing = Timing {
        silence: Duration::from_millis(300),
        ..support::fast_timing()
    };
    let link = Link::start(config).unwrap();
    let mut conn = relay.accept();
    conn.accept_session(&link);
    // While connected a change goes out as a status with what changed.
    link.update_hello(|hello| hello.state = SessionState::PortalSpace);
    assert_eq!(
        conn.read_text(),
        r#"{"type":"status","state":"portal_space"}"#
    );
    // Pings keep it alive past the silence time.
    let alive_until = Instant::now() + Duration::from_millis(600);
    while Instant::now() < alive_until {
        conn.send_frame(true, 9, b"");
        std::thread::sleep(Duration::from_millis(50));
        assert!(link.events().try_recv().is_err(), "pings keep the link");
    }
    let started = Instant::now();
    let (loss, _) = expect_lost(&link);
    assert_eq!(loss, LinkLoss::Silent);
    assert!(started.elapsed() < Duration::from_secs(2));
    // Sending is refused while down.
    assert_eq!(
        link.send_control(EndpointMessage::Attached { request_id: 1 }),
        Err(SendError::NotConnected)
    );
    // A change while down is in the next hello.
    link.send_control(EndpointMessage::Status(Status {
        generation: Some(2),
        character_name: Some("Second".into()),
        ..Default::default()
    }))
    .unwrap();
    let mut conn = relay.accept();
    let hello = conn.accept_session(&link);
    assert_eq!(member(&hello, "character_name"), Some("\"Second\""));
    assert_eq!(member(&hello, "generation"), Some("2"));
    assert_eq!(member(&hello, "state"), Some("\"portal_space\""));
}

#[test]
fn a_change_before_hello_ok_follows_as_a_status() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.upgrade();
    let hello = conn.read_text();
    assert_eq!(member(&hello, "character_guid"), Some("1342177281"));
    link.update_hello(|hello| hello.character_guid = 7);
    conn.send_text(r#"{"type":"hello_ok","protocol":1,"ping_interval_ms":5000}"#);
    expect_connected(&link);
    assert_eq!(conn.read_text(), r#"{"type":"status","character_guid":7}"#);
}

#[test]
fn no_hello_ok_in_time_is_a_failed_attempt() {
    let relay = TestRelay::start();
    let mut config = relay.config();
    config.timing.hello_timeout = Duration::from_millis(200);
    let link = Link::start(config).unwrap();
    let mut conn = relay.accept();
    conn.upgrade();
    conn.read_text();
    // Pings do not stand in for the answer.
    let until = Instant::now() + Duration::from_millis(300);
    while Instant::now() < until {
        conn.send_frame(true, 9, b"");
        std::thread::sleep(Duration::from_millis(40));
    }
    assert_eq!(expect_failed(&link).0, LinkLoss::HelloTimeout);
}

#[test]
fn shutdown_closes_the_connection_promptly() {
    let relay = TestRelay::start();
    let link = relay.link();
    let mut conn = relay.accept();
    conn.accept_session(&link);
    let started = Instant::now();
    link.shutdown();
    let took = started.elapsed();
    assert!(took < Duration::from_millis(500), "{took:?}");
    let frames = conn.read_to_end();
    assert_eq!(frames.last().map(|f| f.opcode), Some(8), "{frames:?}");
}

#[test]
fn dropping_the_link_stops_it_while_it_waits_to_reconnect() {
    let relay = TestRelay::start();
    let mut config = relay.config();
    config.timing.backoff_first = Duration::from_secs(30);
    config.timing.backoff_max = Duration::from_secs(30);
    let link = Link::start(config).unwrap();
    let mut conn = relay.accept();
    conn.read_request();
    conn.send_raw(b"HTTP/1.1 503 Busy\r\n\r\n");
    assert_eq!(expect_failed(&link).1, Duration::from_secs(30));
    let started = Instant::now();
    drop(link);
    assert!(started.elapsed() < Duration::from_millis(500));
}

#[test]
fn shutdown_does_not_wait_for_a_relay_that_never_answers() {
    let relay = TestRelay::start();
    let link = relay.link();
    let _conn = relay.accept();
    // The link waits in its handshake for an answer that never comes.
    std::thread::sleep(Duration::from_millis(50));
    let started = Instant::now();
    link.shutdown();
    assert!(started.elapsed() < Duration::from_millis(500));
}

#[test]
fn the_queue_refuses_binary_when_full_and_sending_needs_a_connection() {
    let relay = TestRelay::start();
    let mut config = relay.config();
    config.queue_bytes = 1000;
    let link = Link::start(config).unwrap();
    assert_eq!(link.send_binary(vec![0; 10]), Err(SendError::NotConnected));
    assert_eq!(
        link.send_control(EndpointMessage::Hello(support::hello())),
        Err(SendError::HelloIsTheLinks)
    );
    let mut conn = relay.accept();
    conn.accept_session(&link);
    assert_eq!(link.send_binary(vec![0; 1001]), Err(SendError::QueueFull));
    assert_eq!(
        link.send_binary(vec![0; 4 * 1024 * 1024 + 1]),
        Err(SendError::TooLarge)
    );
    // A control message too long for one text frame is refused here, not
    // left to break the link.
    assert_eq!(
        link.send_control(EndpointMessage::AttachFailed {
            request_id: 1,
            reason: "x".repeat(64 * 1024),
        }),
        Err(SendError::TooLarge)
    );
    link.send_binary(vec![7; 10]).unwrap();
    assert_eq!(conn.read_frame().unwrap().payload, vec![7; 10]);
}

#[test]
fn nothing_shows_the_token() {
    let relay = TestRelay::start();
    let config = relay.config();
    let shown = format!("{config:?} {config:#?}");
    assert!(!shown.contains(TOKEN), "{shown}");
    let link = Link::start(config).unwrap();
    assert!(!format!("{link:?}").contains(TOKEN));
    let losses = [
        LinkLoss::Connect("x".into()),
        LinkLoss::Tls("x".into()),
        LinkLoss::Handshake(HandshakeError::Status(401)),
        LinkLoss::Refused(RefuseReason::Other("x".into())),
        LinkLoss::ProtocolVersion(2),
        LinkLoss::HelloTimeout,
        LinkLoss::Silent,
        LinkLoss::Ws(WsError::Closed),
        LinkLoss::Unexpected("x"),
        LinkLoss::Closed(None),
    ];
    for loss in losses {
        assert!(!format!("{loss} {loss:?}").contains(TOKEN));
    }
    drop(link);
    let _ = PATIENCE;
}

#[test]
fn an_unreachable_relay_is_a_connect_failure() {
    // A port nothing listens on: bind one, then let it go.
    let port = {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        listener.local_addr().unwrap().port()
    };
    let mut config = TestRelay::start().config();
    config.url = ac_remote::RelayUrl::parse(&format!("ws://127.0.0.1:{port}/")).unwrap();
    let link = Link::start(config).unwrap();
    assert!(matches!(expect_failed(&link).0, LinkLoss::Connect(_)));
}
