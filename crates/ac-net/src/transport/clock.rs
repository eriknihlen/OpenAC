use std::time::Duration;

use crate::NetTime;

/// Length of one interval of the header's time field.
pub const INTERVAL: Duration = Duration::from_millis(500);

/// The half-second counter stamped into the header's time field. It starts
/// at 1 and advances only when [`IntervalClock::update`] runs (once per
/// transport sweep), keeping the fractional remainder; it wraps at 16 bits.
#[derive(Debug, Clone)]
pub struct IntervalClock {
    base: NetTime,
    interval_id: u16,
}

impl IntervalClock {
    /// A clock whose first interval starts at `now`.
    #[must_use]
    pub fn new(now: NetTime) -> Self {
        Self {
            base: now,
            interval_id: 1,
        }
    }

    /// The current interval id.
    #[must_use]
    pub fn interval_id(&self) -> u16 {
        self.interval_id
    }

    /// Advance by every whole interval elapsed since the last advance.
    /// Returns whether the advance reached a time-sync point
    /// ([`crosses_time_sync`]).
    pub fn update(&mut self, now: NetTime) -> bool {
        let elapsed = now.since(self.base);
        if elapsed < INTERVAL {
            return false;
        }
        let steps = elapsed.as_nanos() / INTERVAL.as_nanos();
        let old = self.interval_id;
        // The id is a 16-bit counter: only the steps modulo 65536 matter.
        self.interval_id = self.interval_id.wrapping_add((steps % 65_536) as u16);
        // `steps * INTERVAL` is at most `elapsed`, which fits a Duration.
        self.base += Duration::from_nanos((steps * INTERVAL.as_nanos()) as u64);
        crosses_time_sync(old, u32::try_from(steps).unwrap_or(u32::MAX))
    }
}

/// Intervals between two time-sync points (six half seconds: 3 s).
pub const TIME_SYNC_PERIOD: u32 = 6;

/// Whether advancing the interval counter from `old` by `steps` reaches a
/// time-sync point: the counter lands on a value that is 1 modulo 6, or
/// passes one (it advanced six or more, or its remainder modulo 6 went
/// down without reaching 0). The sum is taken in 32 bits, before the
/// counter's 16-bit wrap.
#[must_use]
pub fn crosses_time_sync(old: u16, steps: u32) -> bool {
    let old = u32::from(old);
    let reached = old.wrapping_add(steps) % TIME_SYNC_PERIOD;
    reached == 1 || steps >= TIME_SYNC_PERIOD || (reached > 0 && reached < old % TIME_SYNC_PERIOD)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Source: OpenAC tests
    // `OutboundReliableTransportTests.TransportClock_StartsAtOne_AdvancesEveryHalfSecond_AndWraps`.
    #[test]
    fn starts_at_one_advances_every_half_second_and_wraps() {
        let mut now = NetTime::ZERO;
        let mut clock = IntervalClock::new(now);
        assert_eq!(clock.interval_id(), 1);

        now += Duration::from_millis(490);
        clock.update(now);
        assert_eq!(clock.interval_id(), 1);
        now += Duration::from_millis(10);
        clock.update(now);
        assert_eq!(clock.interval_id(), 2);

        // A long gap advances by whole intervals and keeps the remainder.
        now += Duration::from_millis(2750);
        clock.update(now);
        assert_eq!(clock.interval_id(), 7);
        now += Duration::from_millis(250);
        clock.update(now);
        assert_eq!(clock.interval_id(), 8);

        // 65531 more intervals take 8 to 3 (mod 65536).
        now += INTERVAL * 65_531;
        clock.update(now);
        assert_eq!(clock.interval_id(), 3);
    }

    // The time-sync point: landing on 1 modulo 6, a jump of six or more,
    // or passing 1 (the remainder went down without reaching 0).
    #[test]
    fn the_time_sync_point_is_every_sixth_interval_or_passed() {
        // One step at a time from 1: 7, 13, 19 (every 3 s).
        let fired: Vec<u16> = (1u16..20)
            .filter(|&id| crosses_time_sync(id - 1, 1))
            .collect();
        assert_eq!(fired, vec![1, 7, 13, 19]);
        // Landing on 0 (mod 6) does not count; passing 1 does.
        assert!(!crosses_time_sync(4, 2)); // 4 -> 6 (0)
        assert!(crosses_time_sync(5, 3)); // 5 -> 8 (2): passed 7
        assert!(crosses_time_sync(4, 4)); // 4 -> 8 (2)
        assert!(!crosses_time_sync(2, 3)); // 2 -> 5
        assert!(!crosses_time_sync(7, 5)); // 7 -> 12 (0)
        assert!(crosses_time_sync(7, 6));
        assert!(crosses_time_sync(0, 1000));
        // The sum is taken before the 16-bit wrap: 65535 % 6 = 3, so
        // 65535 + 2 = 65537 (5) has not passed a point; + 4 lands on 1.
        assert!(!crosses_time_sync(65_535, 2));
        assert!(crosses_time_sync(65_535, 4));
        assert!(crosses_time_sync(65_534, 5)); // 2 -> 1 (65539)
    }

    #[test]
    fn the_clock_reports_the_time_sync_point_as_it_advances() {
        let mut now = NetTime::ZERO;
        let mut clock = IntervalClock::new(now);
        let mut fired = Vec::new();
        for half_second in 1..=13 {
            now += INTERVAL;
            if clock.update(now) {
                fired.push(half_second);
            }
        }
        // Interval 1 at birth: 7 at 3 s, 13 at 6 s.
        assert_eq!(fired, vec![6, 12]);
        // A 2 s stall from 14 to 18 stays below 19.
        now += INTERVAL * 4;
        assert!(!clock.update(now));
        // From a multiple of 6 the remainder cannot go down, so 18 -> 20
        // steps over 19 without a point; 20 -> 25 lands on 1 (mod 6).
        now += INTERVAL * 2;
        assert!(!clock.update(now));
        now += INTERVAL * 5;
        assert!(clock.update(now));
    }

    // Across the 16-bit wrap, one interval at a time: the stored id wraps
    // but each step's test adds in 32 bits, so after 65533 (1 mod 6) the
    // next point is at 1, four intervals later, then every six again.
    #[test]
    fn the_time_sync_points_across_the_counter_wrap() {
        let mut now = NetTime::ZERO;
        let mut clock = IntervalClock::new(now);
        now += INTERVAL * 65_520; // 1 -> 65521, a jump of six or more
        assert!(clock.update(now));
        assert_eq!(clock.interval_id(), 65_521);
        let mut fired = Vec::new();
        for _ in 0..30 {
            now += INTERVAL;
            if clock.update(now) {
                fired.push(clock.interval_id());
            }
        }
        assert_eq!(clock.interval_id(), 15);
        assert_eq!(fired, vec![65_527, 65_533, 1, 7, 13]);

        // Two intervals at once from 65535 land on 1 once wrapped, but the
        // 32-bit sum is 65537 (5 mod 6): no point.
        let mut now = NetTime::ZERO;
        let mut clock = IntervalClock::new(now);
        now += INTERVAL * 65_534;
        clock.update(now);
        assert_eq!(clock.interval_id(), 65_535);
        now += INTERVAL * 2;
        assert!(!clock.update(now));
        assert_eq!(clock.interval_id(), 1);
    }
}
