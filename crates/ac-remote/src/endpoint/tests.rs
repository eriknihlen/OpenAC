use std::time::{Duration, Instant};

use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_runtime::navigation::NavigationView;
use ac_runtime::session::{Generation, LifecycleCommand, PresentationEvent, SessionCommand};

use super::{Action, Codec, Endpoint, INPUT_SILENCE};
use crate::link::SendError;
use crate::protocol::{
    ControlReason, DetachReason, EndpointMessage, LifecyclePermission, ObjectsFilter, Permissions,
    RelayMessage, Subscription,
};

/// One byte per event; a frame's first byte picks the command it decodes to.
#[derive(Debug, Default)]
struct FakeCodec {
    /// Encoding fails.
    broken: bool,
    /// Every event encodes to this many bytes instead of one.
    fat: Option<usize>,
}

const KEYS: u8 = 1;
const BARE_KEYS: u8 = 2;
const LOG_OFF: u8 = 3;
const RECONNECT: u8 = 4;
const CONFIRM_DELETE: u8 = 5;
const CHAT: u8 = 6;
const END_SESSION: u8 = 7;

fn forward() -> SessionCommand {
    SessionCommand::Movement(MovementCommand::Keys(MovementKeys {
        forward: true,
        ..MovementKeys::default()
    }))
}

fn at_generation(command: SessionCommand) -> SessionCommand {
    SessionCommand::AtGeneration {
        generation: Generation::default(),
        command: Box::new(command),
    }
}

impl Codec for FakeCodec {
    fn encode(&mut self, events: &[PresentationEvent]) -> Result<Vec<u8>, String> {
        if self.broken {
            return Err("cannot encode".to_owned());
        }
        Ok(vec![0xEE; events.len() * self.fat.unwrap_or(1)])
    }

    fn decode(&mut self, frame: &[u8]) -> Result<SessionCommand, String> {
        Ok(match frame.first() {
            Some(&KEYS) => at_generation(forward()),
            Some(&BARE_KEYS) => forward(),
            Some(&LOG_OFF) => at_generation(SessionCommand::Lifecycle(LifecycleCommand::LogOff)),
            Some(&RECONNECT) => {
                at_generation(SessionCommand::Lifecycle(LifecycleCommand::Reconnect))
            }
            Some(&CONFIRM_DELETE) => {
                at_generation(SessionCommand::Lifecycle(LifecycleCommand::ConfirmDelete))
            }
            Some(&CHAT) => at_generation(SessionCommand::Chat("hello".to_owned())),
            Some(&END_SESSION) => at_generation(SessionCommand::Lifecycle(
                LifecycleCommand::EndCharacterSession,
            )),
            _ => return Err("not a command".to_owned()),
        })
    }
}

const EVERYTHING: Subscription = Subscription {
    motions: true,
    sound: true,
    effects: true,
    navigation: true,
    world: false,
    objects: ObjectsFilter::All,
};

fn permissions(control: bool, lifecycle: &[LifecyclePermission]) -> Permissions {
    Permissions {
        control,
        lifecycle: lifecycle.to_vec(),
    }
}

fn attach(request_id: u64, subscription: Subscription, permissions: Permissions) -> RelayMessage {
    RelayMessage::Attach {
        request_id,
        subscription,
        permissions,
    }
}

fn endpoint() -> Endpoint<FakeCodec> {
    Endpoint::new(FakeCodec::default(), 1024)
}

/// An endpoint with request 7 attached, the browser allowed to control.
fn attached(lifecycle: &[LifecyclePermission]) -> (Endpoint<FakeCodec>, Instant) {
    let now = Instant::now();
    let mut endpoint = endpoint();
    endpoint.relay(attach(7, EVERYTHING, permissions(true, lifecycle)), now);
    endpoint.attach_done(Ok(()));
    (endpoint, now)
}

/// The same, with the browser in control since `now`.
fn controlling(lifecycle: &[LifecyclePermission]) -> (Endpoint<FakeCodec>, Instant) {
    let (mut endpoint, now) = attached(lifecycle);
    let actions = endpoint.relay(
        RelayMessage::Control {
            request_id: 7,
            on: true,
        },
        now,
    );
    assert!(actions.is_empty(), "{actions:?}");
    (endpoint, now)
}

fn text(line: &str) -> PresentationEvent {
    PresentationEvent::StatusText(line.to_owned())
}

fn controls(actions: &[Action]) -> Vec<EndpointMessage> {
    actions
        .iter()
        .filter_map(|action| match action {
            Action::Control(message) => Some(message.clone()),
            _ => None,
        })
        .collect()
}

fn frames(actions: &[Action]) -> Vec<usize> {
    actions
        .iter()
        .filter_map(|action| match action {
            Action::Binary(bytes) => Some(bytes.len()),
            _ => None,
        })
        .collect()
}

fn detached(reason: DetachReason) -> EndpointMessage {
    EndpointMessage::Detached {
        request_id: 7,
        reason,
    }
}

fn is_release(action: &Action) -> bool {
    matches!(
        action,
        Action::Command(SessionCommand::Movement(MovementCommand::ReleaseKeys))
    )
}

#[test]
fn an_attach_asks_the_session_and_says_attached_once_it_took_it() {
    let mut endpoint = endpoint();
    let actions = endpoint.relay(
        attach(7, EVERYTHING, permissions(true, &[])),
        Instant::now(),
    );
    assert!(matches!(actions[..], [Action::Attach]), "{actions:?}");
    assert!(endpoint.is_attached());
    let actions = endpoint.attach_done(Ok(()));
    assert_eq!(
        controls(&actions),
        [EndpointMessage::Attached { request_id: 7 }]
    );
}

#[test]
fn a_session_that_cannot_attach_says_why_and_nothing_is_attached() {
    let mut endpoint = endpoint();
    endpoint.relay(
        attach(7, EVERYTHING, permissions(true, &[])),
        Instant::now(),
    );
    let actions = endpoint.attach_done(Err("the session has ended".to_owned()));
    assert_eq!(
        controls(&actions),
        [EndpointMessage::AttachFailed {
            request_id: 7,
            reason: "the session has ended".to_owned()
        }]
    );
    assert!(!endpoint.is_attached());
}

#[test]
fn the_sessions_events_go_out_as_one_frame_only_while_attached() {
    let mut endpoint = endpoint();
    assert!(endpoint.events(vec![text("early")]).is_empty());
    let (mut endpoint, _) = attached(&[]);
    let actions = endpoint.events(vec![text("a"), text("b"), text("c")]);
    assert_eq!(frames(&actions), [3]);
}

#[test]
fn the_subscription_leaves_out_what_the_browser_did_not_ask_for() {
    let quiet = Subscription {
        motions: false,
        sound: false,
        effects: false,
        navigation: false,
        world: false,
        objects: ObjectsFilter::All,
    };
    let mut endpoint = endpoint();
    endpoint.relay(attach(7, quiet, permissions(false, &[])), Instant::now());
    endpoint.attach_done(Ok(()));
    let actions = endpoint.events(vec![
        PresentationEvent::Motions(Vec::new()),
        PresentationEvent::Navigation(NavigationView::default()),
        PresentationEvent::DestroyParticles(1),
        text("kept"),
    ]);
    assert_eq!(frames(&actions), [1]);
    // Nothing left: no empty frame.
    let actions = endpoint.events(vec![PresentationEvent::Motions(Vec::new())]);
    assert!(actions.is_empty(), "{actions:?}");
    // And with everything asked for, all of it goes.
    let (mut endpoint, _) = attached(&[]);
    let actions = endpoint.events(vec![
        PresentationEvent::Motions(Vec::new()),
        PresentationEvent::Navigation(NavigationView::default()),
        PresentationEvent::DestroyParticles(1),
    ]);
    assert_eq!(frames(&actions), [3]);
}

#[test]
fn the_browser_leaving_detaches_the_session() {
    let (mut endpoint, now) = attached(&[]);
    // Another request's detach is not ours.
    assert!(
        endpoint
            .relay(RelayMessage::Detach { request_id: 6 }, now)
            .is_empty()
    );
    let actions = endpoint.relay(RelayMessage::Detach { request_id: 7 }, now);
    assert!(matches!(actions[0], Action::Detach), "{actions:?}");
    assert_eq!(controls(&actions), [detached(DetachReason::Browser)]);
    assert!(!endpoint.is_attached());
}

#[test]
fn a_new_attach_replaces_the_one_attached() {
    let (mut endpoint, now) = attached(&[]);
    let actions = endpoint.relay(attach(8, EVERYTHING, permissions(true, &[])), now);
    assert!(
        matches!(
            actions[..],
            [Action::Detach, Action::Control(_), Action::Attach]
        ),
        "{actions:?}"
    );
    assert_eq!(controls(&actions), [detached(DetachReason::Replaced)]);
    assert_eq!(
        controls(&endpoint.attach_done(Ok(()))),
        [EndpointMessage::Attached { request_id: 8 }]
    );
}

#[test]
fn input_is_taken_only_while_the_browser_has_control() {
    let (mut endpoint, now) = attached(&[]);
    assert!(endpoint.frame(&[KEYS], now).is_empty());
    let (mut endpoint, now) = controlling(&[]);
    let actions = endpoint.frame(&[KEYS], now);
    assert!(
        matches!(
            actions[..],
            [Action::Command(SessionCommand::AtGeneration { .. })]
        ),
        "{actions:?}"
    );
}

#[test]
fn a_browser_not_allowed_to_control_is_told_so_and_its_input_dropped() {
    let now = Instant::now();
    let mut endpoint = endpoint();
    endpoint.relay(attach(7, EVERYTHING, permissions(false, &[])), now);
    endpoint.attach_done(Ok(()));
    let actions = endpoint.relay(
        RelayMessage::Control {
            request_id: 7,
            on: true,
        },
        now,
    );
    assert_eq!(
        controls(&actions),
        [EndpointMessage::ControlState {
            request_id: 7,
            controlling: false,
            reason: ControlReason::Released
        }]
    );
    assert!(endpoint.frame(&[KEYS], now).is_empty());
}

#[test]
fn a_second_without_input_lets_go_of_the_keys_and_drops_control() {
    let (mut endpoint, start) = controlling(&[]);
    assert!(endpoint.tick(start + Duration::from_millis(900)).is_empty());
    // Any input frame is the heartbeat.
    endpoint.frame(&[KEYS], start + Duration::from_millis(900));
    assert!(
        endpoint
            .tick(start + Duration::from_millis(1800))
            .is_empty()
    );
    let late = start + Duration::from_millis(900) + INPUT_SILENCE + Duration::from_millis(1);
    let actions = endpoint.tick(late);
    assert!(is_release(&actions[0]), "{actions:?}");
    assert_eq!(
        controls(&actions),
        [EndpointMessage::ControlState {
            request_id: 7,
            controlling: false,
            reason: ControlReason::Heartbeat
        }]
    );
    // Control is gone until the browser takes it again.
    assert!(endpoint.frame(&[KEYS], late).is_empty());
    assert!(endpoint.tick(late + INPUT_SILENCE * 3).is_empty());
}

#[test]
fn releasing_control_lets_go_of_the_keys() {
    let (mut endpoint, now) = controlling(&[]);
    let actions = endpoint.relay(
        RelayMessage::Control {
            request_id: 7,
            on: false,
        },
        now,
    );
    assert!(actions.len() == 1 && is_release(&actions[0]), "{actions:?}");
    assert!(endpoint.frame(&[KEYS], now).is_empty());
}

#[test]
fn bytes_that_are_no_command_end_the_attach() {
    let (mut endpoint, now) = controlling(&[]);
    let actions = endpoint.frame(&[0xFF, 0xFF], now);
    assert!(matches!(actions[0], Action::Detach), "{actions:?}");
    assert_eq!(controls(&actions), [detached(DetachReason::Protocol)]);
    assert!(!endpoint.is_attached());
}

#[test]
fn a_command_not_stamped_with_its_generation_is_dropped() {
    let (mut endpoint, now) = controlling(&[]);
    assert!(endpoint.frame(&[BARE_KEYS], now).is_empty());
    assert!(endpoint.is_attached());
}

#[test]
fn log_off_and_reconnect_need_their_permission_and_delete_is_never_taken() {
    let (mut endpoint, now) = controlling(&[]);
    for frame in [LOG_OFF, END_SESSION, RECONNECT, CONFIRM_DELETE] {
        assert!(endpoint.frame(&[frame], now).is_empty(), "frame {frame}");
    }
    // Ordinary play needs only control.
    assert_eq!(endpoint.frame(&[CHAT], now).len(), 1);

    let (mut endpoint, now) = controlling(&[LifecyclePermission::LogOff]);
    assert_eq!(endpoint.frame(&[LOG_OFF], now).len(), 1);
    assert_eq!(endpoint.frame(&[END_SESSION], now).len(), 1);
    assert!(endpoint.frame(&[RECONNECT], now).is_empty());

    let (mut endpoint, now) =
        controlling(&[LifecyclePermission::LogOff, LifecyclePermission::Reconnect]);
    assert_eq!(endpoint.frame(&[RECONNECT], now).len(), 1);
    assert!(endpoint.frame(&[CONFIRM_DELETE], now).is_empty());
}

#[test]
fn a_lost_link_detaches_the_session_and_says_nothing() {
    let (mut endpoint, _) = controlling(&[]);
    let actions = endpoint.link_lost();
    assert!(matches!(actions[..], [Action::Detach]), "{actions:?}");
    assert!(!endpoint.is_attached());
    assert!(endpoint.link_lost().is_empty());
}

#[test]
fn a_session_that_ended_tells_the_browser() {
    let (mut endpoint, _) = attached(&[]);
    let actions = endpoint.session_ended();
    assert_eq!(controls(&actions), [detached(DetachReason::SessionEnded)]);
    assert!(!endpoint.is_attached());
}

#[test]
fn a_full_outbound_queue_ends_the_attach_as_an_overflow() {
    let (mut endpoint, _) = attached(&[]);
    let actions = endpoint.send_failed(&SendError::QueueFull);
    assert!(matches!(actions[0], Action::Detach), "{actions:?}");
    assert_eq!(controls(&actions), [detached(DetachReason::Overflow)]);
    // A link that is down says so by itself.
    let (mut endpoint, _) = attached(&[]);
    assert!(endpoint.send_failed(&SendError::NotConnected).is_empty());
}

#[test]
fn a_batch_too_large_for_one_frame_is_split() {
    let now = Instant::now();
    let mut endpoint = Endpoint::new(FakeCodec::default(), 2);
    endpoint.relay(attach(7, EVERYTHING, permissions(false, &[])), now);
    endpoint.attach_done(Ok(()));
    let events = (0..5).map(|i| text(&i.to_string())).collect();
    let sizes = frames(&endpoint.events(events));
    assert_eq!(sizes.iter().sum::<usize>(), 5, "{sizes:?}");
    assert!(sizes.iter().all(|&size| size <= 2), "{sizes:?}");
}

#[test]
fn one_event_too_large_for_any_frame_ends_the_attach_as_an_overflow() {
    let now = Instant::now();
    let codec = FakeCodec {
        fat: Some(3),
        ..FakeCodec::default()
    };
    let mut endpoint = Endpoint::new(codec, 2);
    endpoint.relay(attach(7, EVERYTHING, permissions(false, &[])), now);
    endpoint.attach_done(Ok(()));
    let actions = endpoint.events(vec![text("huge")]);
    assert!(matches!(actions[0], Action::Detach), "{actions:?}");
    assert_eq!(controls(&actions), [detached(DetachReason::Overflow)]);
    assert!(frames(&actions).is_empty());
}

#[test]
fn events_that_cannot_be_encoded_end_the_attach() {
    let now = Instant::now();
    let codec = FakeCodec {
        broken: true,
        ..FakeCodec::default()
    };
    let mut endpoint = Endpoint::new(codec, 1024);
    endpoint.relay(attach(7, EVERYTHING, permissions(false, &[])), now);
    endpoint.attach_done(Ok(()));
    let actions = endpoint.events(vec![text("x")]);
    assert!(matches!(actions[0], Action::Detach), "{actions:?}");
    assert_eq!(controls(&actions), [detached(DetachReason::Protocol)]);
}

fn notes(actions: &[Action]) -> Vec<String> {
    actions
        .iter()
        .filter_map(|action| match action {
            Action::Note(line) => Some(line.clone()),
            _ => None,
        })
        .collect()
}

/// An attach that ends by a fault leaves a line for the client's console
/// saying why; one the browser ended does not.
#[test]
fn an_attach_that_ends_by_a_fault_says_why_on_the_console() {
    let (mut endpoint, now) = controlling(&[]);
    let lines = notes(&endpoint.frame(&[0xFF], now));
    assert_eq!(lines.len(), 1, "{lines:?}");
    assert!(lines[0].contains("not a command"), "{lines:?}");

    let codec = FakeCodec {
        broken: true,
        ..FakeCodec::default()
    };
    let mut endpoint = Endpoint::new(codec, 1024);
    endpoint.relay(attach(7, EVERYTHING, permissions(false, &[])), now);
    endpoint.attach_done(Ok(()));
    let lines = notes(&endpoint.events(vec![text("x")]));
    assert_eq!(lines.len(), 1, "{lines:?}");
    assert!(lines[0].contains("cannot encode"), "{lines:?}");

    let (mut endpoint, now) = attached(&[]);
    let actions = endpoint.relay(RelayMessage::Detach { request_id: 7 }, now);
    assert!(notes(&actions).is_empty(), "{actions:?}");
}

// --- the world's own messages ---------------------------------------------

use ac_runtime::session::world_tap::{TappedMessage, WorldTapBatch};
use ac_runtime::world::WorldSnapshot;

const WORLD: Subscription = Subscription {
    world: true,
    ..EVERYTHING
};

fn snapshot() -> PresentationEvent {
    PresentationEvent::Snapshot(WorldSnapshot {
        player_guid: None,
        objects: Vec::new(),
    })
}

fn watching_the_world(max_frame: usize) -> Endpoint<FakeCodec> {
    let mut endpoint = Endpoint::new(FakeCodec::default(), max_frame);
    endpoint.relay(attach(7, WORLD, permissions(false, &[])), Instant::now());
    endpoint.attach_done(Ok(()));
    endpoint
}

fn tapped(client_to_server: bool, body: &[u8]) -> TappedMessage {
    TappedMessage {
        client_to_server,
        body: body.to_vec(),
    }
}

fn binaries(actions: &[Action]) -> Vec<&[u8]> {
    actions
        .iter()
        .filter_map(|action| match action {
            Action::Binary(bytes) => Some(bytes.as_slice()),
            _ => None,
        })
        .collect()
}

/// Every snapshot is a world that starts over (an attach, a new login): a
/// viewer of the world's messages is given them from the start again.
#[test]
fn a_snapshot_starts_the_worlds_messages_over_for_a_viewer_that_asked() {
    let mut endpoint = watching_the_world(1024);
    let actions = endpoint.events(vec![snapshot(), text("x")]);
    assert!(
        actions.iter().any(|a| matches!(a, Action::ListenWorld)),
        "{actions:?}"
    );
    // No snapshot, no restart.
    let actions = endpoint.events(vec![text("y")]);
    assert!(!actions.iter().any(|a| matches!(a, Action::ListenWorld)));
    // A viewer that did not ask gets none.
    let (mut endpoint, _) = attached(&[]);
    let actions = endpoint.events(vec![snapshot()]);
    assert!(!actions.iter().any(|a| matches!(a, Action::ListenWorld)));
}

#[test]
fn the_worlds_messages_go_out_as_records_in_a_frame_of_their_own() {
    let mut endpoint = watching_the_world(1024);
    let batch = WorldTapBatch {
        player: 0x5000_000A,
        messages: vec![
            tapped(false, &[1, 2, 3, 4, 5, 6, 7, 8]),
            tapped(true, &[9, 9, 9, 9, 9]),
        ],
    };
    let actions = endpoint.world(&batch, 0x0102_0304_0506);
    let frames = binaries(&actions);
    assert_eq!(frames.len(), 1, "{actions:?}");
    let mut expected = Vec::new();
    expected.extend_from_slice(b"ACRW");
    expected.extend_from_slice(&0x0102_0304_0506_u64.to_le_bytes());
    // Server to client: time 0, no flags, the player, the length, the body.
    expected.extend_from_slice(&0u32.to_le_bytes());
    expected.extend_from_slice(&0u16.to_le_bytes());
    expected.extend_from_slice(&0u16.to_le_bytes());
    expected.extend_from_slice(&0x5000_000A_u32.to_le_bytes());
    expected.extend_from_slice(&8u32.to_le_bytes());
    expected.extend_from_slice(&[1, 2, 3, 4, 5, 6, 7, 8]);
    // Client to server: flag bit 0, and the body padded to four bytes.
    expected.extend_from_slice(&0u32.to_le_bytes());
    expected.extend_from_slice(&1u16.to_le_bytes());
    expected.extend_from_slice(&0u16.to_le_bytes());
    expected.extend_from_slice(&0x5000_000A_u32.to_le_bytes());
    expected.extend_from_slice(&5u32.to_le_bytes());
    expected.extend_from_slice(&[9, 9, 9, 9, 9, 0, 0, 0]);
    assert_eq!(frames[0], expected.as_slice());
}

#[test]
fn the_worlds_messages_go_nowhere_unasked_or_unattached() {
    let batch = WorldTapBatch {
        player: 1,
        messages: vec![tapped(false, &[1, 2, 3, 4])],
    };
    let (mut endpoint, _) = attached(&[]);
    assert!(endpoint.world(&batch, 5).is_empty());
    let mut unattached = Endpoint::new(FakeCodec::default(), 1024);
    assert!(unattached.world(&batch, 5).is_empty());
    // Nothing to say: no empty frame.
    let empty = WorldTapBatch {
        player: 1,
        messages: Vec::new(),
    };
    assert!(watching_the_world(1024).world(&empty, 5).is_empty());
}

#[test]
fn a_large_world_is_split_into_frames_the_link_takes() {
    // 12 bytes of frame header, then 16 + 8 a record: two records a frame.
    let mut endpoint = watching_the_world(12 + 2 * 24);
    let batch = WorldTapBatch {
        player: 1,
        messages: (0..5).map(|n| tapped(false, &[n; 8])).collect(),
    };
    let actions = endpoint.world(&batch, 5);
    let frames = binaries(&actions);
    assert_eq!(
        frames.iter().map(|f| f.len()).collect::<Vec<_>>(),
        [60, 60, 36]
    );
    assert!(frames.iter().all(|f| f.starts_with(b"ACRW")));
    // One message too large for any frame is left out; the rest goes.
    let mut endpoint = watching_the_world(12 + 24);
    let batch = WorldTapBatch {
        player: 1,
        messages: vec![tapped(false, &[0; 64]), tapped(false, &[7; 8])],
    };
    let frames_len: Vec<usize> = binaries(&endpoint.world(&batch, 5))
        .iter()
        .map(|f| f.len())
        .collect();
    assert_eq!(frames_len, [36]);
}
