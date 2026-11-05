//! The link-status panel's numbers: whether the session is connected, how
//! long since the last packet, the loss percentage, and the round trip of
//! the last answered ping.

use std::time::Duration;

/// What the link-status panel shows.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct LinkStatus {
    /// The session has a connection.
    pub connected: bool,
    /// Seconds since the last valid packet arrived (0 when the clock went
    /// backwards).
    pub seconds_since_last_packet: f64,
    /// The transport's loss percentage.
    pub packet_loss_percent: f64,
    /// The last answered ping's round trip, seconds.
    pub round_trip_seconds: Option<f64>,
}

impl LinkStatus {
    /// No connection: everything zero.
    pub const DISCONNECTED: Self = Self {
        connected: false,
        seconds_since_last_packet: 0.0,
        packet_loss_percent: 0.0,
        round_trip_seconds: None,
    };

    /// The status of a connection whose last packet came at
    /// `last_packet_at`, measured at `now` (both on the session clock).
    #[must_use]
    pub fn connected(
        last_packet_at: Duration,
        now: Duration,
        round_trip_seconds: Option<f64>,
        packet_loss_percent: f64,
    ) -> Self {
        Self {
            connected: true,
            seconds_since_last_packet: now.saturating_sub(last_packet_at).as_secs_f64(),
            packet_loss_percent,
            round_trip_seconds,
        }
    }
}

/// The ping the link panel sends and the round trip of its answer.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PingClock {
    requested_at: Option<Duration>,
    round_trip_seconds: Option<f64>,
}

impl PingClock {
    /// A ping went out at `now`.
    pub fn requested(&mut self, now: Duration) {
        self.requested_at = Some(now);
    }

    /// The answer arrived at `now`: the round trip from the last request,
    /// once (a second answer, or one before any request, changes nothing).
    pub fn answered(&mut self, now: Duration) {
        if let Some(sent) = self.requested_at.take()
            && now >= sent
        {
            self.round_trip_seconds = Some((now - sent).as_secs_f64());
        }
    }

    /// The last measured round trip, seconds.
    #[must_use]
    pub fn round_trip_seconds(&self) -> Option<f64> {
        self.round_trip_seconds
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Net.Tests/WorldSessionLinkStatusTests.cs`.

    use super::*;

    #[test]
    fn a_clock_going_backwards_reads_zero_seconds() {
        // ClockRegression_ClampsPacketAgeToZero.
        let status =
            LinkStatus::connected(Duration::from_secs(2), Duration::from_secs(1), None, 0.0);
        assert!(status.connected);
        assert_eq!(status.seconds_since_last_packet, 0.0);
    }

    #[test]
    fn a_connected_status_carries_loss_and_round_trip() {
        // ConnectedSnapshot_CarriesPacketLossAndRoundTripTelemetry.
        let status = LinkStatus::connected(
            Duration::from_secs(1),
            Duration::from_secs(2),
            Some(0.125),
            2.5,
        );
        assert_eq!(status.seconds_since_last_packet, 1.0);
        assert_eq!(status.packet_loss_percent, 2.5);
        assert_eq!(status.round_trip_seconds, Some(0.125));
        let disconnected = LinkStatus::DISCONNECTED;
        assert!(!disconnected.connected);
    }

    #[test]
    fn a_ping_and_its_answer_measure_the_round_trip_once() {
        // RequestAndEmptyPingResponse_MeasureMonotonicRoundTrip.
        let mut ping = PingClock::default();
        ping.answered(Duration::from_millis(50));
        assert_eq!(ping.round_trip_seconds(), None);
        ping.requested(Duration::from_millis(1_000));
        ping.answered(Duration::from_millis(1_125));
        assert_eq!(ping.round_trip_seconds(), Some(0.125));
        ping.answered(Duration::from_millis(2_000));
        assert_eq!(ping.round_trip_seconds(), Some(0.125));
    }
}
