//! The one clock the plugin tick is raised from.
//!
//! The session loop runs at whatever rate it runs; plugins get a fixed tick.
//! The clock takes in how long each pass of the loop really took, holds the
//! time until a whole step has gone by, then raises one tick per whole step
//! (each worth exactly [`STEP_SECONDS`]) and carries the remainder. A stall
//! (a long load, a debugger) is worth at most [`MAX_STEPS_PER_FEED`] ticks;
//! the rest is dropped. Time held by one run of the session (a generation)
//! is never spent on the next.

/// How much time one plugin tick carries, in seconds.
pub const STEP_SECONDS: f64 = ac_plugin_abi::TICK_SECONDS;

/// The most ticks one feed may raise: the whole steps in 0.2 s, the longest
/// stretch the client's fixed-step clock hands anything at once.
pub const MAX_STEPS_PER_FEED: u32 = 13;

/// The plugin tick clock.
#[derive(Debug, Clone, Default)]
pub struct TickClock {
    held_seconds: f64,
    generation: Option<u64>,
}

impl TickClock {
    /// A clock holding nothing.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The time taken in but not yet spent on a tick, in seconds (always
    /// less than one step).
    #[must_use]
    pub fn held_seconds(&self) -> f64 {
        self.held_seconds
    }

    /// Takes in how long the last pass took (`delta_seconds`; anything not a
    /// finite positive number counts as no time) in run `generation` of the
    /// session (`None` where there is no session), and returns how many
    /// ticks are due now.
    pub fn feed(&mut self, delta_seconds: f64, generation: Option<u64>) -> u32 {
        if let Some(generation) = generation
            && self.generation != Some(generation)
        {
            self.generation = Some(generation);
            self.held_seconds = 0.0;
        }
        if delta_seconds.is_finite() && delta_seconds > 0.0 {
            self.held_seconds += delta_seconds;
        }
        let whole = (self.held_seconds / STEP_SECONDS).min(f64::from(MAX_STEPS_PER_FEED + 1));
        // Truncation toward zero: the whole steps in hand (a non-negative
        // value at most MAX_STEPS_PER_FEED + 1).
        let mut due = whole as u32;
        if due > MAX_STEPS_PER_FEED {
            // So far behind that the time in hand says nothing useful any
            // more: drop all of it rather than carry a debt.
            due = MAX_STEPS_PER_FEED;
            self.held_seconds = 0.0;
        } else {
            self.held_seconds -= f64::from(due) * STEP_SECONDS;
        }
        due
    }

    /// Drops the time held without raising anything.
    pub fn reset(&mut self) {
        self.held_seconds = 0.0;
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Runtime.Tests/Plugins/RuntimePluginTickClockTests.cs`.

    use super::*;

    fn close(a: f64, b: f64) -> bool {
        (a - b).abs() < 1e-9
    }

    #[test]
    fn a_tick_is_raised_only_when_a_whole_step_has_gone_by() {
        let mut clock = TickClock::new();
        assert_eq!(clock.feed(0.007, None), 0);
        assert_eq!(clock.feed(0.008, None), 1);
    }

    #[test]
    fn several_steps_raise_several_ticks() {
        let mut clock = TickClock::new();
        assert_eq!(clock.feed(0.0605, None), 4);
    }

    #[test]
    fn short_feeds_average_the_fixed_rate() {
        let mut clock = TickClock::new();
        let frames = 10_000 / 7;
        let mut ticks = 0u32;
        for _ in 0..frames {
            ticks += clock.feed(0.007, None);
        }
        let expected = (f64::from(frames) * 0.007 / STEP_SECONDS) as u32;
        assert!((expected - 1..=expected + 1).contains(&ticks), "{ticks}");
    }

    #[test]
    fn the_remainder_is_carried_into_the_next_feed() {
        let mut clock = TickClock::new();
        assert_eq!(clock.feed(0.020, None), 1);
        assert!(close(clock.held_seconds(), 0.005));
        assert_eq!(clock.feed(0.010, None), 1);
        assert!(close(clock.held_seconds(), 0.0));
    }

    #[test]
    fn a_feed_of_one_step_is_worth_exactly_one_tick() {
        let mut clock = TickClock::new();
        for _ in 0..1_000 {
            assert_eq!(clock.feed(STEP_SECONDS, None), 1);
        }
        assert!(close(clock.held_seconds(), 0.0));
    }

    #[test]
    fn a_stall_costs_at_most_the_catch_up_bound() {
        let mut clock = TickClock::new();
        assert_eq!(clock.feed(30.0, None), MAX_STEPS_PER_FEED);
        assert_eq!(clock.held_seconds(), 0.0);
        assert_eq!(clock.feed(0.0, None), 0);
    }

    #[test]
    fn the_bound_is_the_steps_in_the_longest_existing_fixed_stretch() {
        assert_eq!((0.2 / STEP_SECONDS) as u32, MAX_STEPS_PER_FEED);
    }

    #[test]
    fn a_time_that_is_not_a_positive_number_of_seconds_counts_as_no_time() {
        for fed in [0.0, -1.0, f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
            let mut clock = TickClock::new();
            clock.feed(0.010, None);
            assert_eq!(clock.feed(fed, None), 0);
            assert!(close(clock.held_seconds(), 0.010));
        }
    }

    #[test]
    fn a_new_session_run_starts_with_nothing_held() {
        let mut clock = TickClock::new();
        assert_eq!(clock.feed(0.010, Some(1)), 0);
        assert!(close(clock.held_seconds(), 0.010));
        // The 10 ms held by the run that ended is not spent on the new one.
        assert_eq!(clock.feed(0.010, Some(2)), 0);
        assert!(close(clock.held_seconds(), 0.010));
    }

    #[test]
    fn reset_drops_the_time_held_without_raising_anything() {
        let mut clock = TickClock::new();
        clock.feed(0.014, None);
        clock.reset();
        assert_eq!(clock.held_seconds(), 0.0);
        assert_eq!(clock.feed(0.0, None), 0);
    }
}
