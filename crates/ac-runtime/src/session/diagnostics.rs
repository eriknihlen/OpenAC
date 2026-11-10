//! The session's network diagnostics, all off by default: a one-line trace
//! of each opcode nothing handles (once per opcode), a dump of every
//! appearance change, and a once-a-second line of inbound pacing and
//! transport counters with a final line when the connection ends. The
//! switches come from the environment (see `content/docs/launch-options.md`); the
//! lines are built here and printed by the session thread on standard error.

use std::collections::HashSet;
use std::time::Duration;

use ac_net::{LinkSnapshot, TransportStats};
use ac_proto::ObjDescEvent;

/// Time per pass spent handling inbound messages while in the world.
pub const INBOUND_BUDGET: Duration = Duration::from_millis(4);

/// Whether a pass that started at `started` and is now at `now` has used
/// its inbound budget. Only the world is bounded: while logging in every
/// message is handled at once, so the handshake drains fully.
#[must_use]
pub fn inbound_budget_exceeded(
    in_world: bool,
    started: Duration,
    now: Duration,
    budget: Duration,
) -> bool {
    in_world && now.saturating_sub(started) >= budget
}

/// Which diagnostics are on.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct NetDiagnostics {
    /// `OPENAC_DUMP_OPCODES=1`: one line per opcode nothing handles, the
    /// first time it arrives.
    pub dump_opcodes: bool,
    /// `OPENAC_DUMP_APPEARANCE=1`: every appearance change, part by part.
    pub dump_appearance: bool,
    /// `OPENAC_PROBE_NET=1`: a `[net-tick]` line every second and a
    /// `[net-final]` line when the connection ends.
    pub probe_net: bool,
    /// `OPENAC_MOTION_LOG=1`: for the player and the selected object, a
    /// `[motion] recv` line per motion message from the server and a
    /// `[motion] applied` line per motion state taken up (the window adds
    /// its own `[motion] window` lines).
    pub motion_log: bool,
    /// `OPENAC_PROBE_EXTERNAL_CONTAINER=1`: one line when the outside
    /// container is asked for, opens, is refused or closes, when the open
    /// one leaves the world, and for each plugin close with its answer.
    pub probe_external_container: bool,
    /// `OPENAC_POSITION_LOG=1`: `[position]` lines for objects within
    /// 30 m of the character (each server position and how it was handled,
    /// and a body that stays more than 4 m from the server's place), and
    /// for each movement the server orders for the character.
    pub position_log: bool,
}

impl NetDiagnostics {
    /// Read the switches through `var` (the process environment, or a test
    /// map). Each is on only when exactly `1`.
    pub fn from_vars(var: impl Fn(&str) -> Option<String>) -> Self {
        let one = |name: &str| var(name).as_deref() == Some("1");
        Self {
            dump_opcodes: one("OPENAC_DUMP_OPCODES"),
            dump_appearance: one("OPENAC_DUMP_APPEARANCE"),
            probe_net: one("OPENAC_PROBE_NET"),
            motion_log: one(MOTION_LOG_VAR),
            probe_external_container: one("OPENAC_PROBE_EXTERNAL_CONTAINER"),
            position_log: one("OPENAC_POSITION_LOG"),
        }
    }

    /// Read the switches from the process environment.
    #[must_use]
    pub fn from_env() -> Self {
        Self::from_vars(|name| std::env::var(name).ok())
    }
}

/// The switch for the motion log (session and window alike).
pub const MOTION_LOG_VAR: &str = "OPENAC_MOTION_LOG";

/// The motion log's line for a motion message from the server: the
/// object, its movement counters, whether it echoes the object's own
/// movement, the movement type, and the interpreted state it carries.
#[must_use]
pub fn received_motion_line(stamp: &str, update: &ac_proto::UpdateMotion) -> String {
    let head = format!(
        "[motion] {stamp} recv 0x{:08X} seq={} ctl={} auto={} type={}",
        update.guid,
        update.movement_sequence,
        update.server_control_sequence,
        u8::from(update.is_autonomous),
        update.movement.movement_type,
    );
    match crate::world::accepted_motion_state(&update.movement) {
        Some(state) => format!("{head} {}", crate::world::motion_line(&state)),
        None => head,
    }
}

/// The opcodes already reported as unhandled.
#[derive(Debug, Default)]
pub struct OpcodeDump {
    seen: HashSet<u32>,
}

impl OpcodeDump {
    /// The line for an opcode nothing handles, the first time it arrives;
    /// `None` after that.
    pub fn unhandled(&mut self, opcode: u32, body_len: usize) -> Option<String> {
        self.seen
            .insert(opcode)
            .then(|| format!("opcodes: unhandled 0x{opcode:04X} (body.len={body_len})"))
    }
}

/// The dump of one appearance change: a header line, then one line per
/// palette swap, texture change and part change.
#[must_use]
pub fn appearance_lines(event: &ObjDescEvent) -> Vec<String> {
    let model = &event.model;
    let mut lines = vec![format!(
        "appearance: 0xF625 guid=0x{:08X} basePal=0x{:08X} subPals={} texChanges={} animParts={}",
        event.guid,
        model.base_palette_id.unwrap_or(0),
        model.sub_palettes.len(),
        model.texture_changes.len(),
        model.anim_part_changes.len()
    )];
    for sp in &model.sub_palettes {
        lines.push(format!(
            "  SP id=0x{:08X} offset={} length={}",
            sp.sub_palette_id, sp.offset, sp.length
        ));
    }
    for tc in &model.texture_changes {
        lines.push(format!(
            "  TC part={:02} oldTex=0x{:08X} -> newTex=0x{:08X}",
            tc.part_index, tc.old_texture, tc.new_texture
        ));
    }
    for apc in &model.anim_part_changes {
        lines.push(format!(
            "  APC part={:02} -> gfx=0x{:08X}",
            apc.part_index, apc.new_model_id
        ));
    }
    lines
}

/// The dump line for an appearance change that did not parse.
#[must_use]
pub fn appearance_parse_failed(body_len: usize) -> String {
    format!("appearance: 0xF625 PARSE FAILED body.len={body_len}")
}

/// One second of the session loop, as the `[net-tick]` line reports it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NetTickWindow {
    /// The window's length, seconds.
    pub seconds: f64,
    /// Inbound messages handled.
    pub processed: u64,
    /// Inbound messages waiting for the next pass.
    pub queue_depth: usize,
    /// Passes whose inbound handling stopped at the budget.
    pub budget_breaks: u64,
    /// The longest gap between two passes, milliseconds.
    pub max_gap_ms: f64,
    /// Datagrams sent.
    pub sends: u64,
    /// Acknowledgements sent.
    pub acks: u64,
    /// Packets resent on the server's request.
    pub resends: u64,
    /// NAK packets sent.
    pub naks_out: u64,
    /// NAK packets received.
    pub naks_in: u64,
    /// Reject packets received.
    pub rejects_in: u64,
    /// Inbound duplicates dropped.
    pub dup_drops: u64,
    /// Keystream words parked beside a gap.
    pub parked: u64,
    /// Parked words reclaimed after a reject.
    pub reclaimed: u64,
    /// Sent packets still cached for resend.
    pub cache_depth: usize,
    /// Missing inbound sequences.
    pub nak_set_depth: usize,
}

/// The `[net-tick]` line: rates per second over the window, depths as they
/// are now, then the session phase.
#[must_use]
pub fn format_net_tick_line(w: &NetTickWindow, state: &str) -> String {
    let rate = |count: u64| count as f64 / w.seconds;
    format!(
        "[net-tick] in/s={:.0} q={} budget-breaks={} maxgap={:.0}ms out/s={:.0} acks/s={:.0} \
         resend/s={:.0} nak-out/s={:.0} nak-in/s={:.0} rej-in/s={:.0} dup-drop/s={:.0} \
         parked/s={:.0} reclaim/s={:.0} cache={} nakset={} st={state}",
        rate(w.processed),
        w.queue_depth,
        w.budget_breaks,
        w.max_gap_ms,
        rate(w.sends),
        rate(w.acks),
        rate(w.resends),
        rate(w.naks_out),
        rate(w.naks_in),
        rate(w.rejects_in),
        rate(w.dup_drops),
        rate(w.parked),
        rate(w.reclaimed),
        w.cache_depth,
        w.nak_set_depth,
    )
}

/// The `[net-final]` line: the connection's totals when it ends.
#[must_use]
pub fn format_net_final_line(link: &LinkSnapshot) -> String {
    let s = &link.stats;
    format!(
        "[net-final] resends={} nak-in={} nak-out={} rej-in={} acks-out={} acks-in={} \
         dup-drop={} sanity-drop={} cksum-fail={} parked={} reclaimed={} uncached-nak={} \
         cache={} nakset={}",
        s.resends_sent,
        s.nak_requests_received,
        s.naks_sent,
        s.rejects_received,
        s.acks_sent,
        s.acks_consumed,
        s.inbound_dups_dropped,
        s.inbound_sanity_drops,
        s.checksum_failures,
        s.keys_parked,
        s.reject_words_reclaimed,
        s.uncached_nak_ids,
        link.cache_depth,
        link.nak_set,
    )
}

/// What one pass of the session loop reports to the probe.
#[derive(Debug, Clone, Copy)]
pub struct NetPass<'a> {
    /// When the pass started (session time).
    pub started: Duration,
    /// Inbound messages handled in the pass.
    pub processed: u64,
    /// The pass stopped handling at the budget.
    pub budget_broke: bool,
    /// Inbound messages left for the next pass.
    pub queue_depth: usize,
    /// Datagrams sent so far on this connection.
    pub sent_total: u64,
    /// The transport as it is now.
    pub link: &'a LinkSnapshot,
}

/// The `[net-tick]` probe: gathers each pass and yields a line once a
/// second.
#[derive(Debug, Default)]
pub struct NetTickProbe {
    last_pass: Option<Duration>,
    window_start: Option<Duration>,
    max_gap: Duration,
    processed: u64,
    budget_breaks: u64,
    sent_seen: u64,
    seen: TransportStats,
}

impl NetTickProbe {
    /// Count one pass; a line when a second has gone by since the window
    /// began.
    pub fn pass(&mut self, pass: &NetPass<'_>, state: &str) -> Option<String> {
        if let Some(last) = self.last_pass {
            self.max_gap = self.max_gap.max(pass.started.saturating_sub(last));
        }
        self.last_pass = Some(pass.started);
        self.processed += pass.processed;
        if pass.budget_broke {
            self.budget_breaks += 1;
        }
        let Some(window_start) = self.window_start else {
            self.window_start = Some(pass.started);
            return None;
        };
        let window = pass.started.saturating_sub(window_start);
        if window < Duration::from_secs(1) {
            return None;
        }
        let now = &pass.link.stats;
        let delta = |current: u64, seen: u64| current.saturating_sub(seen);
        let line = format_net_tick_line(
            &NetTickWindow {
                seconds: window.as_secs_f64(),
                processed: self.processed,
                queue_depth: pass.queue_depth,
                budget_breaks: self.budget_breaks,
                max_gap_ms: self.max_gap.as_secs_f64() * 1000.0,
                sends: delta(pass.sent_total, self.sent_seen),
                acks: delta(now.acks_sent, self.seen.acks_sent),
                resends: delta(now.resends_sent, self.seen.resends_sent),
                naks_out: delta(now.naks_sent, self.seen.naks_sent),
                naks_in: delta(now.nak_requests_received, self.seen.nak_requests_received),
                rejects_in: delta(now.rejects_received, self.seen.rejects_received),
                dup_drops: delta(now.inbound_dups_dropped, self.seen.inbound_dups_dropped),
                parked: delta(now.keys_parked, self.seen.keys_parked),
                reclaimed: delta(now.reject_words_reclaimed, self.seen.reject_words_reclaimed),
                cache_depth: pass.link.cache_depth,
                nak_set_depth: pass.link.nak_set,
            },
            state,
        );
        self.window_start = Some(pass.started);
        self.max_gap = Duration::ZERO;
        self.processed = 0;
        self.budget_breaks = 0;
        self.sent_seen = pass.sent_total;
        self.seen = now.clone();
        Some(line)
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Net.Tests/WorldSessionInboundBudgetTests.cs`
    //! and `NetProbeTests.cs`; the dump formats are `WorldSession`'s
    //! `ACDREAM_DUMP_OPCODES` / `ACDREAM_DUMP_APPEARANCE` lines.

    use super::*;
    use ac_proto::{AnimPartChange, ModelData, SubPalette, TextureChange};

    fn ticks(n: u64) -> Duration {
        Duration::from_micros(n)
    }

    #[test]
    fn in_world_over_budget_stops() {
        assert!(inbound_budget_exceeded(
            true,
            ticks(100),
            ticks(250),
            ticks(100)
        ));
    }

    #[test]
    fn in_world_at_the_budget_boundary_stops() {
        assert!(inbound_budget_exceeded(
            true,
            ticks(0),
            ticks(100),
            ticks(100)
        ));
    }

    #[test]
    fn in_world_under_budget_continues() {
        assert!(!inbound_budget_exceeded(
            true,
            ticks(0),
            ticks(99),
            ticks(100)
        ));
    }

    #[test]
    fn not_in_world_never_bounds_so_the_handshake_drains_fully() {
        assert!(!inbound_budget_exceeded(
            false,
            ticks(0),
            ticks(1_000_000),
            ticks(100)
        ));
    }

    #[test]
    fn the_budget_is_four_milliseconds() {
        assert_eq!(INBOUND_BUDGET, Duration::from_millis(4));
    }

    #[test]
    fn the_net_tick_line_carries_the_transport_fields() {
        let line = format_net_tick_line(
            &NetTickWindow {
                seconds: 2.0,
                processed: 10,
                queue_depth: 3,
                budget_breaks: 1,
                max_gap_ms: 17.4,
                sends: 8,
                acks: 2,
                resends: 4,
                naks_out: 6,
                naks_in: 8,
                rejects_in: 2,
                dup_drops: 10,
                parked: 12,
                reclaimed: 2,
                cache_depth: 5,
                nak_set_depth: 7,
            },
            "InWorld",
        );
        assert_eq!(
            line,
            "[net-tick] in/s=5 q=3 budget-breaks=1 maxgap=17ms out/s=4 \
             acks/s=1 resend/s=2 nak-out/s=3 nak-in/s=4 rej-in/s=1 \
             dup-drop/s=5 parked/s=6 reclaim/s=1 cache=5 nakset=7 st=InWorld"
        );
    }

    fn link(acks: u64, resends: u64) -> LinkSnapshot {
        LinkSnapshot {
            stats: TransportStats {
                acks_sent: acks,
                resends_sent: resends,
                ..TransportStats::default()
            },
            nak_set: 2,
            reclaimed_words: 0,
            cache_depth: 9,
            pending_resends: 0,
            highest_id_received: 0,
            highest_id_sent: 0,
            ack_watermark: 0,
            partial_messages: 0,
            packet_loss_percent: 0.0,
            handshake_confirmed: true,
        }
    }

    fn pass(ms: u64, processed: u64, broke: bool, sent: u64, link: &LinkSnapshot) -> NetPass<'_> {
        NetPass {
            started: Duration::from_millis(ms),
            processed,
            budget_broke: broke,
            queue_depth: 4,
            sent_total: sent,
            link,
        }
    }

    #[test]
    fn the_probe_emits_one_line_a_second_with_the_windows_counts() {
        // Counters run from the connection's start, so the first line counts
        // everything before it too (OpenAC's window deltas start at zero).
        let mut probe = NetTickProbe::default();
        let first = link(10, 1);
        assert_eq!(probe.pass(&pass(0, 2, false, 100, &first), "InWorld"), None);
        assert_eq!(
            probe.pass(&pass(400, 3, true, 104, &first), "InWorld"),
            None
        );
        let later = link(12, 3);
        let line = probe
            .pass(&pass(1000, 5, true, 110, &later), "InWorld")
            .expect("a second went by");
        assert_eq!(
            line,
            "[net-tick] in/s=10 q=4 budget-breaks=2 maxgap=600ms out/s=110 \
             acks/s=12 resend/s=3 nak-out/s=0 nak-in/s=0 rej-in/s=0 \
             dup-drop/s=0 parked/s=0 reclaim/s=0 cache=9 nakset=2 st=InWorld"
        );
        // The next window starts from here.
        assert_eq!(
            probe.pass(&pass(1500, 1, false, 111, &later), "InWorld"),
            None
        );
    }

    #[test]
    fn the_final_line_names_every_counter() {
        let line = format_net_final_line(&link(7, 3));
        for field in [
            "[net-final] resends=3",
            " nak-in=0",
            " nak-out=0",
            " rej-in=0",
            " acks-out=7",
            " acks-in=0",
            " dup-drop=0",
            " sanity-drop=0",
            " cksum-fail=0",
            " parked=0",
            " reclaimed=0",
            " uncached-nak=0",
            " cache=9",
            " nakset=2",
        ] {
            assert!(line.contains(field), "{field} missing from {line}");
        }
    }

    #[test]
    fn an_unhandled_opcode_is_reported_once() {
        let mut dump = OpcodeDump::default();
        assert_eq!(
            dump.unhandled(0xF7B1, 12).as_deref(),
            Some("opcodes: unhandled 0xF7B1 (body.len=12)")
        );
        assert_eq!(dump.unhandled(0xF7B1, 40), None);
        assert_eq!(
            dump.unhandled(0x0024, 4).as_deref(),
            Some("opcodes: unhandled 0x0024 (body.len=4)")
        );
    }

    #[test]
    fn the_appearance_dump_lists_every_change() {
        let event = ObjDescEvent {
            guid: 0x5000_0001,
            model: ModelData {
                base_palette_id: Some(0x0400_007E),
                sub_palettes: vec![SubPalette {
                    sub_palette_id: 0x0400_1234,
                    offset: 3,
                    length: 7,
                }],
                texture_changes: vec![TextureChange {
                    part_index: 2,
                    old_texture: 0x0500_0001,
                    new_texture: 0x0500_0002,
                }],
                anim_part_changes: vec![AnimPartChange {
                    part_index: 9,
                    new_model_id: 0x0100_0042,
                }],
            },
            instance_sequence: 1,
            obj_desc_sequence: 2,
        };
        assert_eq!(
            appearance_lines(&event),
            [
                "appearance: 0xF625 guid=0x50000001 basePal=0x0400007E subPals=1 texChanges=1 animParts=1",
                "  SP id=0x04001234 offset=3 length=7",
                "  TC part=02 oldTex=0x05000001 -> newTex=0x05000002",
                "  APC part=09 -> gfx=0x01000042",
            ]
        );
        assert_eq!(
            appearance_parse_failed(5),
            "appearance: 0xF625 PARSE FAILED body.len=5"
        );
    }

    fn vars(pairs: &'static [(&'static str, &'static str)]) -> impl Fn(&str) -> Option<String> {
        move |name| {
            pairs
                .iter()
                .find(|(k, _)| *k == name)
                .map(|(_, v)| (*v).to_owned())
        }
    }

    #[test]
    fn each_switch_is_on_only_when_exactly_one() {
        assert_eq!(
            NetDiagnostics::from_vars(vars(&[])),
            NetDiagnostics::default()
        );
        assert_eq!(
            NetDiagnostics::from_vars(vars(&[
                ("OPENAC_DUMP_OPCODES", "1"),
                ("OPENAC_DUMP_APPEARANCE", "yes"),
                ("OPENAC_PROBE_NET", "1"),
                ("OPENAC_MOTION_LOG", "1"),
                ("OPENAC_PROBE_EXTERNAL_CONTAINER", "1"),
                ("OPENAC_POSITION_LOG", "1"),
            ])),
            NetDiagnostics {
                dump_opcodes: true,
                dump_appearance: false,
                probe_net: true,
                motion_log: true,
                probe_external_container: true,
                position_log: true,
            }
        );
    }

    #[test]
    fn a_received_swing_reads_as_its_stance_forward_command_and_counters() {
        let update = ac_proto::UpdateMotion {
            guid: 0x5000_000A,
            instance_sequence: 1,
            movement_sequence: 7,
            server_control_sequence: 2,
            is_autonomous: false,
            movement: ac_proto::MovementData {
                movement_type: 0,
                motion_flags: 0,
                current_style: 0x003E,
                movement: ac_proto::Movement::Interpreted {
                    state: ac_proto::InterpretedMotionState {
                        current_style: Some(0x003E),
                        forward_command: Some(0x005B),
                        forward_speed: Some(1.25),
                        ..ac_proto::InterpretedMotionState::default()
                    },
                    sticky_object: None,
                },
            },
        };
        assert_eq!(
            received_motion_line("t=7.250", &update),
            "[motion] t=7.250 recv 0x5000000A seq=7 ctl=2 auto=0 type=0 style=0x8000003E \
             fwd=0x1000005B@1.25 side=0x00000000@1.00 turn=0x00000000@1.00"
        );
    }
}
