//! The session's frame clock: a count of passes, which always moves, and
//! the world's simulation time, which moves only while there is a world to
//! simulate. Between giving up one world and standing up the next (through
//! a portal, or arriving at login) elapsed time means nothing; time run
//! through the gap would land in one lump on the first pass after it, so
//! the simulation clock stands still and carries on where it stopped.

/// One pass of the clock.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FrameTime {
    /// Passes so far, this one included.
    pub frame_number: u64,
    /// This pass's step, seconds (0 for a step that is not a finite,
    /// positive number).
    pub delta_seconds: f64,
    /// The world's simulation time after this pass, seconds.
    pub simulation_seconds: f64,
}

/// The frame clock. See the module documentation.
#[derive(Debug, Clone, Default)]
pub struct FrameClock {
    frame_number: u64,
    simulation_seconds: f64,
}

impl FrameClock {
    /// Passes so far.
    #[must_use]
    pub fn frame_number(&self) -> u64 {
        self.frame_number
    }

    /// The world's simulation time, seconds.
    #[must_use]
    pub fn simulation_seconds(&self) -> f64 {
        self.simulation_seconds
    }

    /// Take one pass of `delta_seconds`; the simulation time moves only
    /// when `world_available`.
    pub fn advance(&mut self, delta_seconds: f64, world_available: bool) -> FrameTime {
        let delta_seconds = normalize_delta_seconds(delta_seconds);
        self.frame_number = self.frame_number.saturating_add(1);
        if world_available {
            self.simulation_seconds += delta_seconds;
        }
        FrameTime {
            frame_number: self.frame_number,
            delta_seconds,
            simulation_seconds: self.simulation_seconds,
        }
    }
}

/// A step that is not a finite, positive number of seconds (up to the
/// largest single-precision value) counts as none.
#[must_use]
pub fn normalize_delta_seconds(delta_seconds: f64) -> f64 {
    if delta_seconds.is_finite() && delta_seconds > 0.0 && delta_seconds <= f64::from(f32::MAX) {
        delta_seconds
    } else {
        0.0
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `src/AcDream.Runtime/GameRuntimeClock.cs` and
    //! `tests/AcDream.HostParity.Tests/Scenarios/FrameClockParityTests.cs`
    //! (`TheWorldClockStandsStillWhileThereIsNoWorldOnBothClients`).

    use super::*;

    const TICK: f64 = 1.0 / 30.0;

    #[test]
    fn the_world_clock_stands_still_while_there_is_no_world() {
        let mut clock = FrameClock::default();
        clock.advance(TICK, true);
        let after_one = clock.simulation_seconds();
        assert!(after_one > 0.0);
        clock.advance(TICK, false);
        clock.advance(TICK, false);
        assert_eq!(clock.simulation_seconds(), after_one, "held");
        assert_eq!(clock.frame_number(), 3, "the passes still count");
        let t = clock.advance(TICK, true);
        assert!(
            (t.simulation_seconds - after_one - TICK).abs() < 1e-9,
            "carries on where it stopped"
        );
    }

    #[test]
    fn steps_that_are_not_finite_positive_seconds_count_as_none() {
        for bad in [0.0, -1.0, f64::NAN, f64::INFINITY, f64::MAX] {
            assert_eq!(normalize_delta_seconds(bad), 0.0, "{bad}");
        }
        assert_eq!(normalize_delta_seconds(0.25), 0.25);
    }
}
