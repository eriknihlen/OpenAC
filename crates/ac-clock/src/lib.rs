//! The clock the UI reads, the same code natively and in a browser.
//!
//! On `wasm32-unknown-unknown` the standard library has no clock:
//! `std::time::Instant::now()` and `SystemTime::now()` panic, and an
//! `Instant` cannot be made any other way. A module built for the web
//! imports nothing, so the page that hosts it hands the time in instead.
//!
//! - [`Instant`] is `std::time::Instant` natively (the same type, so nothing
//!   changes there). On wasm32 it is this crate's own type with the same
//!   methods and operators, counted from the page's time zero; its
//!   [`Instant::now`] is the time last handed in with [`set_now`].
//! - [`unix_time`] and [`unix_millis`] are the wall clock: the system's
//!   natively, on wasm32 the value last handed in with [`set_unix_millis`].
//!
//! [`set_now`] and [`set_unix_millis`] exist natively too, as no-ops (the
//! system clock is read there), so a host written for the page also builds
//! and is tested natively.

use std::time::Duration;

#[cfg(any(target_arch = "wasm32", test))]
mod page;

/// A point on the monotonic clock: `std::time::Instant` itself.
#[cfg(not(target_arch = "wasm32"))]
pub type Instant = std::time::Instant;

#[cfg(target_arch = "wasm32")]
pub use page::Instant;

/// Hand in the page's time: how long since its time zero (a page's
/// `performance.now()`). Every [`Instant::now`] returns it until the next
/// call. Natively a no-op: the system clock is read there.
#[cfg(not(target_arch = "wasm32"))]
pub fn set_now(_since_page_start: Duration) {}

/// Hand in the page's time: how long since its time zero (a page's
/// `performance.now()`). Every [`Instant::now`] returns it until the next
/// call.
#[cfg(target_arch = "wasm32")]
pub fn set_now(since_page_start: Duration) {
    page::set_now(since_page_start);
}

/// Hand in the wall clock, as milliseconds since 1970-01-01 UTC (a page's
/// `Date.now()`). Natively a no-op: the system clock is read there.
#[cfg(not(target_arch = "wasm32"))]
pub fn set_unix_millis(_millis: i64) {}

/// Hand in the wall clock, as milliseconds since 1970-01-01 UTC (a page's
/// `Date.now()`); a time before 1970 counts as 1970.
#[cfg(target_arch = "wasm32")]
pub fn set_unix_millis(millis: i64) {
    page::set_unix_millis(millis);
}

/// The wall clock: how long since 1970-01-01 UTC (zero for a clock set
/// before then).
#[cfg(not(target_arch = "wasm32"))]
#[must_use]
pub fn unix_time() -> Duration {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or(Duration::ZERO)
}

/// The wall clock: how long since 1970-01-01 UTC, as last handed in with
/// [`set_unix_millis`] (zero before that).
#[cfg(target_arch = "wasm32")]
#[must_use]
pub fn unix_time() -> Duration {
    page::unix_time()
}

/// The wall clock in whole milliseconds since 1970-01-01 UTC (zero for a
/// clock set before then; `i64::MAX` past what that holds).
#[must_use]
pub fn unix_millis() -> i64 {
    i64::try_from(unix_time().as_millis()).unwrap_or(i64::MAX)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn takes_std(at: std::time::Instant) -> std::time::Instant {
        at
    }

    #[test]
    fn natively_the_instant_is_the_standard_one() {
        let now = Instant::now();
        assert_eq!(takes_std(now), now);
        let standard: std::time::Instant = Instant::now();
        assert!(standard >= now);
    }

    #[test]
    fn natively_the_wall_clock_is_the_systems() {
        let before = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap();
        let millis = unix_millis();
        let after = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap();
        assert!(i64::try_from(before.as_millis()).unwrap() <= millis);
        assert!(millis <= i64::try_from(after.as_millis()).unwrap());
        assert!(unix_time() >= before);
    }

    #[test]
    fn natively_handing_in_the_time_changes_nothing() {
        set_now(Duration::from_secs(5));
        set_unix_millis(0);
        assert!(
            unix_millis() > 1_600_000_000_000,
            "still the system's clock"
        );
    }
}
