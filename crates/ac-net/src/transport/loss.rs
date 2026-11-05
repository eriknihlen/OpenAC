use std::time::Duration;

use super::TransportStats;
use crate::NetTime;

/// Samples kept in the rolling window.
pub const WINDOW_SIZE: usize = 40;
/// One sample is taken per this period.
pub const SNAPSHOT_PERIOD: Duration = Duration::from_secs(2);

#[derive(Debug, Clone, Copy, Default)]
struct Sample {
    sent: u16,
    retransmitted: u16,
    received: u16,
    naked: u16,
}

/// The link-status packet-loss figure: over the last 40 two-second samples,
/// `100 × (NAK ids sent + resends) / (packets received + packets sent)`.
/// Each sample's counts are 16-bit deltas, as the original keeps them.
#[derive(Debug)]
pub struct PacketLossAverager {
    samples: [Sample; WINDOW_SIZE],
    snapshot_at: NetTime,
    last: [u64; 4],
    totals: [u64; 4],
    next: usize,
    count: usize,
}

fn counters(stats: &TransportStats) -> [u64; 4] {
    [
        stats.packets_sent,
        stats.resends_sent,
        stats.packets_received,
        stats.nak_ids_sent,
    ]
}

impl PacketLossAverager {
    /// Baselines taken from `stats` at `now`.
    pub fn new(now: NetTime, stats: &TransportStats) -> Self {
        Self {
            samples: [Sample::default(); WINDOW_SIZE],
            snapshot_at: now,
            last: counters(stats),
            totals: [0; 4],
            next: 0,
            count: 0,
        }
    }

    /// The loss percentage (0 with no traffic).
    pub fn percentage(&self) -> f64 {
        let [sent, retransmitted, received, naked] = self.totals;
        let denominator = received + sent;
        if denominator == 0 {
            0.0
        } else {
            100.0 * (naked + retransmitted) as f64 / denominator as f64
        }
    }

    /// Take a sample if a period has passed (one per call at most; skipped
    /// periods are not filled with empty samples).
    pub fn sweep(&mut self, now: NetTime, stats: &TransportStats) {
        if now.since(self.snapshot_at) < SNAPSHOT_PERIOD {
            return;
        }
        self.snapshot_at = now;
        let current = counters(stats);
        let mut delta = [0u16; 4];
        for i in 0..4 {
            // Truncated to 16 bits like the original's sample fields.
            delta[i] = current[i].saturating_sub(self.last[i]) as u16;
            self.last[i] = current[i];
        }
        let sample = Sample {
            sent: delta[0],
            retransmitted: delta[1],
            received: delta[2],
            naked: delta[3],
        };
        if self.count == WINDOW_SIZE {
            let old = self.samples[self.next];
            self.apply(old, false);
        } else {
            self.count += 1;
        }
        self.samples[self.next] = sample;
        self.next = (self.next + 1) % WINDOW_SIZE;
        self.apply(sample, true);
    }

    fn apply(&mut self, sample: Sample, add: bool) {
        let values = [
            sample.sent,
            sample.retransmitted,
            sample.received,
            sample.naked,
        ];
        for (total, value) in self.totals.iter_mut().zip(values) {
            if add {
                *total += u64::from(value);
            } else {
                *total -= u64::from(value);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(millis: u64) -> NetTime {
        NetTime::from_millis(millis)
    }

    // Source for this module's tests: OpenAC tests `Transport/RetailPacketLossAveragerTests.cs`.

    #[test]
    fn no_sample_before_the_period() {
        let mut stats = TransportStats {
            packets_sent: 5,
            nak_ids_sent: 1,
            ..TransportStats::default()
        };
        let mut sut = PacketLossAverager::new(at(0), &stats);
        stats.packets_sent += 10;
        stats.nak_ids_sent += 2;
        sut.sweep(at(1900), &stats);
        assert_eq!(sut.percentage(), 0.0);
    }

    #[test]
    fn naks_plus_resends_over_received_plus_sent() {
        let mut stats = TransportStats::default();
        let mut sut = PacketLossAverager::new(at(0), &stats);
        stats.packets_sent = 60;
        stats.packets_received = 40;
        stats.nak_ids_sent = 2;
        stats.resends_sent = 3;
        sut.sweep(at(2000), &stats);
        assert_eq!(sut.percentage(), 5.0);
    }

    #[test]
    fn the_oldest_sample_leaves_after_forty_periods() {
        let mut stats = TransportStats::default();
        let mut sut = PacketLossAverager::new(at(0), &stats);
        stats.packets_sent = 10;
        stats.nak_ids_sent = 10;
        let mut now = 2000;
        sut.sweep(at(now), &stats);
        for _ in 0..WINDOW_SIZE {
            stats.packets_sent += 10;
            now += 2000;
            sut.sweep(at(now), &stats);
        }
        assert_eq!(sut.percentage(), 0.0);
    }

    #[test]
    fn zero_traffic_is_zero() {
        let stats = TransportStats::default();
        let mut sut = PacketLossAverager::new(at(0), &stats);
        sut.sweep(at(2000), &stats);
        assert_eq!(sut.percentage(), 0.0);
    }
}
