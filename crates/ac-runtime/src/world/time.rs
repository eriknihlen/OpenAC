//! Game time: the server's tick count at the last time sync, advanced by
//! the session's own clock between syncs (one tick per second).

use std::time::Duration;

use ac_core::world::{CalendarDate, CalendarError, DerethCalendar};

/// The session's view of game time.
#[derive(Debug, Clone, Default)]
pub struct GameClock {
    calendar: DerethCalendar,
    synced: Option<(f64, Duration)>,
}

impl GameClock {
    /// Ticks advanced per second of session time.
    pub const TICK_SIZE: f64 = 1.0;

    /// The calendar the ticks are read with.
    #[must_use]
    pub fn calendar(&self) -> &DerethCalendar {
        &self.calendar
    }

    /// Take the server's tick count, received at session time `now`.
    pub fn sync_from_server(&mut self, server_ticks: f64, now: Duration) {
        self.synced = Some((server_ticks, now));
    }

    /// Game ticks at session time `now`, once a sync arrived.
    #[must_use]
    pub fn now_ticks(&self, now: Duration) -> Option<f64> {
        self.synced
            .map(|(ticks, at)| ticks + now.saturating_sub(at).as_secs_f64() * Self::TICK_SIZE)
    }

    /// The calendar date at session time `now`.
    pub fn date(&self, now: Duration) -> Option<Result<CalendarDate, CalendarError>> {
        self.now_ticks(now).map(|t| self.calendar.to_calendar(t))
    }

    /// Whether it is day at session time `now`.
    pub fn is_daytime(&self, now: Duration) -> Option<Result<bool, CalendarError>> {
        self.now_ticks(now).map(|t| self.calendar.is_daytime(t))
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `WorldTimeService`: ticks advance with wall time after a sync.
    use super::*;

    #[test]
    fn ticks_advance_one_per_second_after_a_sync() {
        let mut clock = GameClock::default();
        assert_eq!(clock.now_ticks(Duration::from_secs(5)), None);
        clock.sync_from_server(1000.0, Duration::from_secs(10));
        assert_eq!(clock.now_ticks(Duration::from_secs(10)), Some(1000.0));
        assert_eq!(clock.now_ticks(Duration::from_millis(12_500)), Some(1002.5));
        clock.sync_from_server(2000.0, Duration::from_secs(20));
        assert_eq!(clock.now_ticks(Duration::from_secs(21)), Some(2001.0));
    }

    #[test]
    fn date_comes_from_the_calendar() {
        let mut clock = GameClock::default();
        clock.sync_from_server(0.0, Duration::ZERO);
        let date = clock.date(Duration::ZERO).unwrap().unwrap();
        assert_eq!(date, DerethCalendar::default().to_calendar(0.0).unwrap());
    }
}
