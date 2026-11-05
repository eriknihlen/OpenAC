//! A real connection against the server model, on virtual time.

use std::cell::Cell;
use std::rc::Rc;
use std::time::Duration;

use crate::lossy::{LossDirection, LossOptions, LossyFilter};
use crate::packet::{PacketFlags, PacketHeader};
use crate::test_support::link::Dir;
use crate::test_support::server::{ServerModel, SessionState, TerminationReason};
use crate::test_support::{
    CLIENT_ID, CLIENT_SEED, COOKIE, Harness, SERVER_SEED, is_connect_response, message, text_of,
};
use crate::transport::seq::newest;
use crate::{NetTime, packet};

const GAME_ACTION: u32 = 0xF7B1;
const SERVER_MESSAGE: u32 = 0xF7E0;

fn ms(millis: u64) -> Duration {
    Duration::from_millis(millis)
}

fn talk(text: &str) -> Vec<u8> {
    message(GAME_ACTION, text)
}

fn server_text(text: &str) -> Vec<u8> {
    message(SERVER_MESSAGE, text)
}

/// Make sure anything due (acks, time syncs) has left the server, so a
/// scripted drop hits the next message.
fn settle(h: &mut Harness) {
    h.pump_server();
}

// Source: OpenAC tests `InboundSequenceTrackerTests.CleanLifecycle_ZeroNaks_ZeroSpuriousDrops`.
#[test]
fn clean_lifecycle_has_no_inbound_faults_and_the_first_sealed_server_packet_is_two() {
    let mut h = Harness::connected();
    for _ in 0..10 {
        h.step(ms(100));
    }
    h.server_send(server_text("clean lifecycle"));
    h.deliver();
    assert!(h.received_texts().contains(&"clean lifecycle".to_string()));

    let s = h.conn().snapshot();
    assert_eq!(s.nak_set, 0);
    assert_eq!(s.stats.keys_parked, 0);
    assert_eq!(s.stats.inbound_dups_dropped, 0);
    assert_eq!(s.stats.inbound_sanity_drops, 0);
    assert_eq!(s.stats.checksum_failures, 0);

    let mut min_sealed = u32::MAX;
    let mut max_sequence = 0;
    for datagram in &h.server.sent {
        let header = PacketHeader::from_bytes(datagram).unwrap();
        if header.flags.has(PacketFlags::ENCRYPTED_CHECKSUM) && header.sequence != 0 {
            min_sealed = min_sealed.min(header.sequence);
        }
        max_sequence = newest(max_sequence, header.sequence);
    }
    assert_eq!(min_sealed, 2);
    assert_eq!(s.highest_id_received, max_sequence);
    assert_eq!(h.server.crc_drops, 0);
    assert_eq!(h.connect_port_sends, 1);
}

// Source: OpenAC tests `OutboundReliableTransportTests.LostGameAction_IsResentOnNak_AllMessagesDispatchInOrder`
// and `RebuiltResend_VerifiesUnderAceCrypto_WithTheOriginalKey`.
#[test]
fn a_lost_client_packet_is_resent_on_the_server_nak_with_its_original_key() {
    let mut h = Harness::connected();
    let base = h.server.dispatched.len();
    let mut expected = Vec::new();
    for i in 0..10 {
        if i == 4 {
            h.link.drop_next(Dir::ClientToServer, 1);
        }
        let text = format!("msg {i}");
        h.send(&talk(&text));
        expected.push(text);
    }
    for _ in 0..20 {
        h.step(ms(10));
    }
    assert_eq!(h.dispatched_texts()[base..], expected[..]);
    assert_eq!(h.link.dropped(Dir::ClientToServer), 1);
    let stats = h.conn().stats().clone();
    assert_eq!(stats.resends_sent, 1);
    assert_eq!(stats.nak_requests_received, 1);
    assert_eq!(stats.uncached_nak_ids, 0);
    assert!(!h.server.is_terminated());
    assert_eq!(h.server.crc_drops, 0);
    assert_eq!(h.server.duplicate_drops, 0);
    assert_eq!(h.server.crypto.headroom(), 256);
    assert_eq!(h.server.crypto.orphan_count(), 0);
}

/// A 480-byte message (the size of a character options block) goes out as
/// two fragments in two packets; the server model, which reassembles client
/// fragments by fragment sequence the way the emulator does, dispatches it
/// once, whole, in order with the messages around it. Losing the first
/// fragment's packet heals through the ordinary NAK and resend.
#[test]
fn a_message_over_one_fragment_reaches_the_server_whole_even_when_a_fragment_is_lost() {
    for drop_first_fragment in [false, true] {
        let mut h = Harness::connected();
        let base = h.server.dispatched.len();
        let big = talk(&"o".repeat(476));
        assert_eq!(big.len(), 480);
        h.send(&talk("before"));
        if drop_first_fragment {
            h.link.drop_next(Dir::ClientToServer, 1);
        }
        h.send(&big);
        h.send(&talk("after 1"));
        h.send(&talk("after 2"));
        for _ in 0..20 {
            h.step(ms(10));
        }
        let dispatched = &h.server.dispatched[base..];
        assert_eq!(dispatched.len(), 4, "drop={drop_first_fragment}");
        assert_eq!(text_of(&dispatched[0]), "before");
        assert_eq!(dispatched[1], big);
        assert_eq!(text_of(&dispatched[2]), "after 1");
        assert_eq!(text_of(&dispatched[3]), "after 2");
        let stats = h.conn().stats().clone();
        assert_eq!(stats.resends_sent, u64::from(drop_first_fragment));
        assert_eq!(stats.uncached_nak_ids, 0);
        assert!(!h.server.is_terminated());
        assert_eq!(h.server.crc_drops, 0);
        assert_eq!(h.server.duplicate_drops, 0);
        assert_eq!(h.server.fragment_gate_buffer_count(), 0);
        assert_eq!(h.server.crypto.orphan_count(), 0);
    }
}

// Source: OpenAC tests `AckNakSchedulerTests.ModelAcceptsAck_AtReusedSequence_WithoutAdvancingWatermark`.
#[test]
fn the_server_accepts_our_ack_at_a_reused_sequence() {
    let mut h = Harness::connected();
    h.send(&talk("one"));
    h.send(&talk("two"));
    assert_eq!(h.server.last_received_packet_sequence(), 3);
    let acks_before = h.conn().stats().acks_sent;
    for _ in 0..5 {
        h.step(ms(500));
    }
    assert!(h.conn().stats().acks_sent > acks_before);
    assert_eq!(h.server.duplicate_drops, 0);
    assert_eq!(h.server.crc_drops, 0);
    assert_eq!(h.server.state_drops, 0);
    assert_eq!(h.server.last_received_packet_sequence(), 3);
}

// Source: OpenAC tests `AckNakSchedulerTests.QuietSession_CumulativeAcksKeepAceAlive_PastThe60sHorizon`.
#[test]
fn a_quiet_session_stays_alive_on_cumulative_acks() {
    let mut h = Harness::connected();
    for _ in 0..240 {
        h.step(ms(500));
    }
    assert!(!h.server.is_terminated());
    let margin = h.server.timeout_deadline.since(h.now);
    assert!(
        margin > Duration::from_secs(50),
        "deadline margin {margin:?}"
    );
    let acks = h.conn().stats().acks_sent;
    assert!(acks >= 40, "only {acks} acks in 120 s");
    assert_eq!(h.server.crc_drops, 0);
    assert_eq!(h.server.state_drops, 0);
    assert_eq!(h.server.duplicate_drops, 0);
    assert_eq!(h.conn().snapshot().nak_set, 0);
}

// Source: OpenAC tests `AckNakSchedulerTests.FullLifecycle_CleanRun_FloodCollapses_GracefulTeardown`.
#[test]
fn a_flood_collapses_to_one_ack_and_disconnect_ends_the_session() {
    let mut h = Harness::connected();
    let acks_before = h.conn().stats().acks_sent;
    for i in 0..20 {
        h.server_send(server_text(&format!("flood {i}")));
    }
    h.deliver();
    h.tick();
    assert_eq!(h.received.len(), 22); // the two welcome messages + 20
    assert_eq!(h.conn().stats().acks_sent, acks_before);
    h.now += Duration::from_secs(2);
    h.tick();
    assert_eq!(h.conn().stats().acks_sent, acks_before + 1);
    for _ in 0..10 {
        h.step(ms(500));
    }
    assert!(!h.server.is_terminated());
    assert_eq!(h.server.crc_drops, 0);
    assert_eq!(h.server.crypto.headroom(), 256);
    assert_eq!(h.conn().snapshot().nak_set, 0);

    h.conn().disconnect();
    h.flush_client();
    assert_eq!(
        h.server.termination_reason(),
        Some(TerminationReason::PacketHeaderDisconnect)
    );
}

// Source: OpenAC tests `NakEmissionTests.S2CLoss_NakRoundTrip_ModelRetransmission_AcksResume`.
#[test]
fn a_lost_server_packet_is_naked_resent_and_acks_resume() {
    let mut h = Harness::connected();
    settle(&mut h);
    h.deliver();
    h.link.drop_next(Dir::ServerToClient, 1);
    h.server_send(server_text("lost"));
    h.server_send(server_text("marker"));
    h.deliver();
    assert_eq!(h.conn().snapshot().nak_set, 1);
    assert!(h.received_texts().contains(&"marker".to_string()));

    let naks = h.conn().stats().naks_sent;
    let acks = h.conn().stats().acks_sent;
    let served = h.server.retransmits_served;
    h.now += ms(700);
    h.tick();
    assert_eq!(h.conn().stats().naks_sent, naks + 1);
    assert_eq!(h.server.retransmits_served, served + 1);
    assert!(h.server.sent.iter().any(|d| {
        PacketHeader::from_bytes(d)
            .unwrap()
            .flags
            .has(PacketFlags::RETRANSMISSION)
    }));
    h.deliver();
    assert!(h.received_texts().contains(&"lost".to_string()));
    assert_eq!(h.conn().snapshot().nak_set, 0);
    assert_eq!(h.conn().stats().checksum_failures, 0);
    assert_eq!(h.server.crc_drops, 0);

    h.now += ms(2100);
    h.tick();
    assert!(h.conn().stats().acks_sent > acks);
    assert!(!h.server.is_terminated());
}

// Source: OpenAC tests `NakEmissionTests.LongLoss_NaksOnTheGateCadence_NoAcks_GapHealsInsideTheWindow`.
#[test]
fn a_long_outage_naks_on_the_gate_cadence_with_no_acks_then_heals() {
    let mut h = Harness::connected();
    settle(&mut h);
    h.deliver();
    h.link.drop_next(Dir::ServerToClient, 1);
    h.server_send(server_text("lost"));
    h.server_send(server_text("marker"));
    h.deliver();
    assert_eq!(h.conn().snapshot().nak_set, 1);
    let naks = h.conn().stats().naks_sent;
    let acks = h.conn().stats().acks_sent;

    let blocked = Rc::new(Cell::new(true));
    let gate = blocked.clone();
    h.link
        .drop_when(Dir::ServerToClient, move |_, _| gate.get());
    for _ in 0..40 {
        h.step(ms(250));
    }
    let during = h.conn().stats().naks_sent - naks;
    assert!((11..=15).contains(&during), "{during} NAKs in 10 s");
    assert_eq!(h.conn().stats().acks_sent, acks);
    assert!(!h.server.is_terminated());

    blocked.set(false);
    for _ in 0..50 {
        if h.conn().snapshot().nak_set == 0 && h.received_texts().contains(&"lost".to_string()) {
            break;
        }
        h.step(ms(700));
    }
    assert!(h.received_texts().contains(&"lost".to_string()));
    assert_eq!(h.conn().snapshot().nak_set, 0);
    assert_eq!(h.conn().stats().checksum_failures, 0);
    h.now += ms(2100);
    h.tick();
    assert!(h.conn().stats().acks_sent > acks);
    assert!(!h.server.is_terminated());
}

// Source: OpenAC tests `NakEmissionTests.PrunedId_RejectAtFreshSequence_ReclaimKeepsTheStreamAligned`.
#[test]
fn a_pruned_id_is_rejected_and_the_reclaim_keeps_the_stream_aligned() {
    let mut h = Harness::connected();
    settle(&mut h);
    h.deliver();
    h.link.drop_next(Dir::ServerToClient, 1);
    h.server_send(server_text("victim"));
    h.server_send(server_text("marker"));
    h.deliver();
    assert_eq!(h.conn().snapshot().nak_set, 1);

    let blocked = Rc::new(Cell::new(true));
    let gate = blocked.clone();
    h.link
        .drop_when(Dir::ServerToClient, move |_, _| gate.get());
    for step in 0..260 {
        h.now += ms(500);
        h.send(&talk(&format!("keepalive {step}")));
        h.pump_server();
        h.deliver();
        h.tick();
    }
    assert!(!h.server.is_terminated());
    assert!(h.server.retransmits_served > 0);

    blocked.set(false);
    for _ in 0..200 {
        if h.conn().snapshot().nak_set == 0 {
            break;
        }
        h.step(ms(700));
    }
    let s = h.conn().snapshot();
    assert_eq!(s.nak_set, 0);
    assert!(s.stats.reject_words_reclaimed >= 1);
    assert!(h.server.sent.iter().any(|d| {
        PacketHeader::from_bytes(d)
            .unwrap()
            .flags
            .has(PacketFlags::REJECT_RETRANSMIT)
    }));
    assert!(!h.received_texts().contains(&"victim".to_string()));

    let failures = s.stats.checksum_failures;
    let drain = s.reclaimed_words + 3;
    for i in 0..drain {
        let text = format!("post-heal {i}");
        h.server_send(server_text(&text));
        h.deliver();
        assert!(h.received_texts().contains(&text), "{text} not received");
    }
    let s = h.conn().snapshot();
    assert_eq!(s.stats.checksum_failures, failures);
    assert_eq!(s.reclaimed_words, 0);
    assert_eq!(s.nak_set, 0);
    assert_eq!(h.server.crypto.headroom(), 256);
    assert!(!h.server.is_terminated());
}

// Source: OpenAC tests `InboundSequenceTrackerTests.S2CLoss_LaterPacketsStillDecode_LateRedeliveryCompletesTheMessage`.
#[test]
fn a_multi_fragment_server_message_completes_on_late_redelivery() {
    let mut h = Harness::connected();
    settle(&mut h);
    h.deliver();
    let before = h.received.len();
    let big = "x".repeat(600);
    h.link.drop_next(Dir::ServerToClient, 1);
    h.server_send(server_text(&big));
    h.deliver();
    let s = h.conn().snapshot();
    assert_eq!(s.nak_set, 1);
    assert_eq!(s.stats.keys_parked, 1);
    assert_eq!(s.partial_messages, 1);
    assert_eq!(h.received.len(), before);

    h.server_send(server_text("after the loss"));
    h.deliver();
    assert_eq!(h.received_texts()[before..], ["after the loss".to_string()]);

    let missing = h.conn().snapshot().highest_id_received - 2;
    let dropped = h
        .server
        .sent
        .iter()
        .find(|d| PacketHeader::from_bytes(d).unwrap().sequence == missing)
        .unwrap()
        .clone();
    h.inject_to_client(dropped);
    h.deliver();
    assert_eq!(h.received_texts()[before + 1], big);
    let s = h.conn().snapshot();
    assert_eq!(s.nak_set, 0);
    assert_eq!(s.partial_messages, 0);
    assert_eq!(s.stats.inbound_dups_dropped, 0);
    assert_eq!(s.stats.checksum_failures, 0);
}

// Source: OpenAC tests `ConnectResponseRetransmitTests.Connect_FirstConnectResponseDropped_RetryHealsHandshake`.
#[test]
fn a_dropped_connect_response_is_retried_and_heals_the_handshake() {
    let mut h = Harness::new();
    let sent = Rc::new(Cell::new(0));
    let counter = sent.clone();
    h.link.drop_when(Dir::ClientToServer, move |_, d| {
        if !is_connect_response(d) {
            return false;
        }
        counter.set(counter.get() + 1);
        counter.get() == 1
    });
    h.connect();
    assert_eq!(sent.get(), 2);
    assert_eq!(h.server.connect_responses_accepted, 1);
    assert_eq!(h.server.state_drops, 0);
    assert_eq!(h.server.crc_drops, 0);
    assert_eq!(h.server.duplicate_drops, 0);
    assert_eq!(h.server.crypto.headroom(), 256);
}

// Source: OpenAC tests `ConnectResponseRetransmitTests.Connect_CleanHandshake_SendsExactlyOneConnectResponse`.
#[test]
fn a_clean_handshake_sends_exactly_one_connect_response() {
    let mut h = Harness::connected();
    for _ in 0..20 {
        h.step(ms(100));
    }
    assert_eq!(h.connect_port_sends, 1);
    assert_eq!(h.server.state_drops, 0);
    assert_eq!(h.received.len(), 2);
}

// Source: OpenAC tests `ConnectResponseRetransmitTests.Connect_ServerResponsesLost_RetriesDropHarmlessly_SessionHeals`.
#[test]
fn lost_server_responses_make_retries_that_drop_harmlessly_and_the_session_heals() {
    let mut h = Harness::new();
    for index in 1..=3 {
        h.link.drop_at(Dir::ServerToClient, index);
    }
    h.connect();
    for _ in 0..20 {
        h.step(ms(100));
    }
    assert_eq!(h.server.connect_responses_accepted, 1);
    assert!(h.connect_port_sends >= 2, "{} sends", h.connect_port_sends);
    assert!(h.server.state_drops >= 1);
    assert_eq!(h.server.crc_drops, 0);
    assert_eq!(h.server.duplicate_drops, 0);
    let s = h.conn().snapshot();
    assert!(s.stats.naks_sent >= 1);
    assert!(s.stats.keys_parked >= 2);
    assert_eq!(s.stats.checksum_failures, 0);
    assert_eq!(s.nak_set, 0);
    assert_eq!(h.received.len(), 2, "both welcome messages healed");
    assert_eq!(h.server.crypto.headroom(), 256);
}

// Source: OpenAC tests `ConnectResponseRetransmitTests.AceModel_DuplicateConnectResponse_AfterAcceptance_DropsViaCheckState`.
#[test]
fn the_server_model_drops_a_duplicate_connect_response_by_state() {
    let now = NetTime::ZERO;
    let mut model = ServerModel::new(now, CLIENT_SEED, SERVER_SEED, CLIENT_ID, COOKIE);
    let response = packet::encode(
        PacketHeader {
            sequence: 1,
            flags: PacketFlags::CONNECT_RESPONSE,
            ..PacketHeader::default()
        },
        &COOKIE.to_le_bytes(),
        None,
    )
    .unwrap();
    model.send_connect_request(now);
    assert_eq!(model.state, SessionState::AwaitingConnectResponse);
    model.receive(now, &response);
    assert_eq!(model.state, SessionState::Connected);
    assert_eq!(model.connect_responses_accepted, 1);
    model.receive(now, &response);
    assert_eq!(model.connect_responses_accepted, 1);
    assert_eq!(model.state_drops, 1);
    assert_eq!(model.crc_drops, 0);
    assert_eq!(model.crypto.headroom(), 256);
}

/// The end-to-end gate: seeded 5 % loss in each direction, thousands of
/// messages each way, every message delivered exactly once (client to
/// server also in order — the server gates on fragment order; server to
/// client arrives in arrival order, as the transport has no reorder
/// buffer), both recovery paths exercised, and the keystream-health
/// counters at zero.
///
/// Source: OpenAC tests `NakEmissionTests.LossSoak_TwoPercentBidirectional_ZeroMessageLoss_LedgersConverge`
/// and `LossyTransportDecoratorTests.LossySession_FivePercentSeeded_ZeroMessageLoss_Headroom256`.
#[test]
fn five_percent_loss_both_ways_every_message_exactly_once() {
    const MESSAGES: usize = 3000;
    let mut h = Harness::new();
    h.lossy = LossyFilter::new(LossOptions {
        drop_percent: 5,
        seed: 0x5EED_0005,
        direction: LossDirection::Both,
    })
    .unwrap();
    h.connect();
    let welcome = h.received.len();

    for i in 0..MESSAGES {
        h.now += ms(25);
        h.send(&talk(&format!("c2s {i}")));
        let mut text = format!("s2c {i}|");
        if i % 100 == 7 {
            // Every hundredth message spans three fragments.
            text.push_str(&"y".repeat(1000));
        }
        h.server_send(server_text(&text));
        h.deliver();
        h.tick();
        if i % 500 == 0 {
            assert!(
                h.server.crypto.headroom() >= 240,
                "headroom {} at message {i}",
                h.server.crypto.headroom()
            );
            assert!(!h.server.is_terminated(), "terminated at message {i}");
        }
    }

    let count =
        |texts: Vec<String>, prefix: &str| texts.iter().filter(|t| t.starts_with(prefix)).count();
    // Tail losses heal on the next traffic, so keep a trickle going.
    for trickle in 0..240 {
        let s2c = count(h.received_texts(), "s2c ");
        let c2s = count(h.dispatched_texts(), "c2s ");
        let s = h.conn().snapshot();
        if s2c == MESSAGES
            && c2s == MESSAGES
            && s.nak_set == 0
            && s.reclaimed_words == 0
            && s.pending_resends == 0
            && s.cache_depth <= 1
            && h.server.out_of_order_packet_count() == 0
        {
            break;
        }
        h.now += ms(500);
        h.send(&talk(&format!("trickle {trickle}")));
        h.pump_server();
        h.deliver();
        h.tick();
    }

    // Exactly once, both ways; client to server in order.
    let expected_c2s: Vec<String> = (0..MESSAGES).map(|i| format!("c2s {i}")).collect();
    let dispatched_c2s: Vec<String> = h
        .dispatched_texts()
        .into_iter()
        .filter(|t| t.starts_with("c2s "))
        .collect();
    assert_eq!(dispatched_c2s, expected_c2s);

    let mut received_s2c: Vec<usize> = h.received_texts()[welcome..]
        .iter()
        .filter_map(|t| t.strip_prefix("s2c "))
        .map(|t| t.split('|').next().unwrap().parse().unwrap())
        .collect();
    let arrived_out_of_order = received_s2c.windows(2).filter(|w| w[0] > w[1]).count();
    received_s2c.sort_unstable();
    assert_eq!(received_s2c, (0..MESSAGES).collect::<Vec<_>>());

    // The loss was real, in both directions, and both recoveries fired.
    let loss = h.lossy.as_ref().unwrap().counters();
    assert!(loss.outbound_dropped > 50, "{loss:?}");
    assert!(loss.inbound_dropped > 50, "{loss:?}");
    let s = h.conn().snapshot();
    assert!(s.stats.resends_sent > 0 && s.stats.nak_requests_received > 0);
    assert!(s.stats.naks_sent > 0 && h.server.retransmits_served > 0);
    assert!(arrived_out_of_order > 0);

    // Keystream health.
    assert_eq!(s.stats.checksum_failures, 0, "{s:?}");
    assert_eq!(s.stats.uncached_nak_ids, 0, "{s:?}");
    assert_eq!(s.stats.inbound_sanity_drops, 0, "{s:?}");
    assert_eq!(h.server.crc_drops, 0);
    assert_eq!(h.server.crypto.headroom(), 256);

    // Converged.
    assert_eq!(s.nak_set, 0);
    assert_eq!(s.reclaimed_words, 0);
    assert_eq!(s.pending_resends, 0);
    assert!(s.cache_depth <= 1, "cache depth {}", s.cache_depth);
    assert_eq!(s.partial_messages, 0);
    assert_eq!(h.server.out_of_order_packet_count(), 0);
    assert_eq!(h.server.fragment_gate_buffer_count(), 0);
    assert!(!h.server.is_terminated());
}
