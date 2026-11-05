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
    pub fn update(&mut self, now: NetTime) {
        let elapsed = now.since(self.base);
        if elapsed < INTERVAL {
            return;
        }
        let steps = elapsed.as_nanos() / INTERVAL.as_nanos();
        // The id is a 16-bit counter: only the steps modulo 65536 matter.
        self.interval_id = self.interval_id.wrapping_add((steps % 65_536) as u16);
        // `steps * INTERVAL` is at most `elapsed`, which fits a Duration.
        self.base += Duration::from_nanos((steps * INTERVAL.as_nanos()) as u64);
    }
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
}
