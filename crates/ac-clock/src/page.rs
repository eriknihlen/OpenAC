//! The browser's clock: instants counted from the page's time zero, and
//! the wall clock, both as the host page last handed them in.
//!
//! Built for wasm32, and natively for its tests.

use std::cell::Cell;
use std::ops::{Add, AddAssign, Sub, SubAssign};
use std::time::Duration;

thread_local! {
    /// The page's time, as last handed in.
    static NOW: Cell<Duration> = const { Cell::new(Duration::ZERO) };
    /// The wall clock, as last handed in.
    static UNIX: Cell<Duration> = const { Cell::new(Duration::ZERO) };
}

/// Hand in the page's time since its time zero.
pub fn set_now(since_page_start: Duration) {
    NOW.with(|now| now.set(since_page_start));
}

/// Hand in the wall clock as milliseconds since 1970 (earlier counts as 1970).
pub fn set_unix_millis(millis: i64) {
    let since = Duration::from_millis(u64::try_from(millis).unwrap_or(0));
    UNIX.with(|unix| unix.set(since));
}

/// The wall clock as last handed in.
pub fn unix_time() -> Duration {
    UNIX.with(Cell::get)
}

/// A point on the page's monotonic clock, with the standard `Instant`'s
/// methods and operators.
///
/// It is held as signed nanoseconds from the page's time zero, so an
/// instant from before the page started (a session clock that began hours
/// earlier, sent as an offset) is as valid as a later one.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Instant {
    nanos: i128,
}

/// The nanoseconds of `d`.
fn nanos_of(d: Duration) -> i128 {
    // A Duration holds at most u64::MAX seconds: well inside i128.
    i128::try_from(d.as_nanos()).unwrap_or(i128::MAX)
}

/// The duration of `nanos`, which must not be negative; `None` past what a
/// Duration holds.
fn duration_of(nanos: i128) -> Option<Duration> {
    let nanos = u128::try_from(nanos).ok()?;
    let secs = u64::try_from(nanos / 1_000_000_000).ok()?;
    let sub = u32::try_from(nanos % 1_000_000_000).ok()?;
    Some(Duration::new(secs, sub))
}

impl Instant {
    /// The page's time as last handed in with [`crate::set_now`].
    #[must_use]
    pub fn now() -> Self {
        Self {
            nanos: nanos_of(NOW.with(Cell::get)),
        }
    }

    /// The time from `earlier` to this one; zero when `earlier` is later.
    #[must_use]
    pub fn duration_since(&self, earlier: Self) -> Duration {
        self.saturating_duration_since(earlier)
    }

    /// The time from `earlier` to this one, or `None` when `earlier` is
    /// later.
    #[must_use]
    pub fn checked_duration_since(&self, earlier: Self) -> Option<Duration> {
        duration_of(self.nanos.checked_sub(earlier.nanos)?)
    }

    /// The time from `earlier` to this one; zero when `earlier` is later.
    #[must_use]
    pub fn saturating_duration_since(&self, earlier: Self) -> Duration {
        self.checked_duration_since(earlier).unwrap_or_default()
    }

    /// The time since this instant, by the page's time now.
    #[must_use]
    pub fn elapsed(&self) -> Duration {
        Self::now().duration_since(*self)
    }

    /// `duration` later, or `None` if that is out of range.
    #[must_use]
    pub fn checked_add(&self, duration: Duration) -> Option<Self> {
        Some(Self {
            nanos: self.nanos.checked_add(nanos_of(duration))?,
        })
    }

    /// `duration` earlier, or `None` if that is out of range.
    #[must_use]
    pub fn checked_sub(&self, duration: Duration) -> Option<Self> {
        Some(Self {
            nanos: self.nanos.checked_sub(nanos_of(duration))?,
        })
    }
}

impl Add<Duration> for Instant {
    type Output = Self;
    /// # Panics
    /// If the result is out of range, as the standard `Instant` does.
    fn add(self, rhs: Duration) -> Self {
        self.checked_add(rhs)
            .expect("overflow when adding duration to instant")
    }
}

impl AddAssign<Duration> for Instant {
    fn add_assign(&mut self, rhs: Duration) {
        *self = *self + rhs;
    }
}

impl Sub<Duration> for Instant {
    type Output = Self;
    /// # Panics
    /// If the result is out of range, as the standard `Instant` does.
    fn sub(self, rhs: Duration) -> Self {
        self.checked_sub(rhs)
            .expect("overflow when subtracting duration from instant")
    }
}

impl SubAssign<Duration> for Instant {
    fn sub_assign(&mut self, rhs: Duration) {
        *self = *self - rhs;
    }
}

impl Sub<Instant> for Instant {
    type Output = Duration;
    /// The time from `rhs` to this one; zero when `rhs` is later.
    fn sub(self, rhs: Self) -> Duration {
        self.duration_since(rhs)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ms(n: u64) -> Duration {
        Duration::from_millis(n)
    }

    #[test]
    fn now_is_the_time_last_handed_in() {
        set_now(ms(1_500));
        let a = Instant::now();
        assert_eq!(Instant::now(), a, "the clock stands until the next call");
        set_now(ms(2_250));
        let b = Instant::now();
        assert_eq!(b.duration_since(a), ms(750));
        assert_eq!(b - a, ms(750));
        assert_eq!(a.elapsed(), ms(750));
        assert!(a < b);
    }

    #[test]
    fn a_later_earlier_saturates_or_is_refused() {
        set_now(ms(100));
        let early = Instant::now();
        let late = early + ms(40);
        assert_eq!(early.duration_since(late), Duration::ZERO);
        assert_eq!(early.saturating_duration_since(late), Duration::ZERO);
        assert_eq!(early.checked_duration_since(late), None);
        assert_eq!(late.checked_duration_since(early), Some(ms(40)));
        assert_eq!(early - late, Duration::ZERO);
    }

    #[test]
    fn instants_before_the_pages_time_zero_are_valid() {
        set_now(ms(2_000));
        let now = Instant::now();
        let session_start = now - Duration::from_secs(3 * 3600);
        assert_eq!(
            now.duration_since(session_start),
            Duration::from_secs(3 * 3600)
        );
        assert_eq!(
            now.checked_sub(Duration::from_secs(3 * 3600)),
            Some(session_start)
        );
        assert_eq!(session_start + Duration::from_secs(3 * 3600), now);
    }

    #[test]
    fn arithmetic_matches_the_standard_instant() {
        set_now(ms(10));
        let mut t = Instant::now();
        t += ms(5);
        assert_eq!(t, Instant::now() + ms(5));
        t -= ms(15);
        assert_eq!(t, Instant::now() - ms(10));
        let d = Duration::new(1, 999_999_999);
        assert_eq!((t + d) - t, d, "nanoseconds survive");
        assert_eq!(t.checked_add(d).unwrap().checked_sub(d), Some(t));
        let far = Duration::new(u64::MAX, 999_999_999);
        assert!(t.checked_add(far).is_some(), "a Duration always fits");
        assert_eq!(
            t.checked_add(far).unwrap().checked_duration_since(t),
            Some(far)
        );
    }

    #[test]
    fn instants_order_and_hash_by_time() {
        use std::collections::HashSet;
        set_now(ms(7));
        let a = Instant::now();
        let set: HashSet<Instant> = [a, a + ms(1), a].into_iter().collect();
        assert_eq!(set.len(), 2);
        assert_eq!([a + ms(1), a].iter().min(), Some(&a));
    }

    #[test]
    fn the_wall_clock_is_the_value_last_handed_in() {
        assert_eq!(unix_time(), Duration::ZERO, "nothing handed in yet");
        set_unix_millis(1_731_330_000_123);
        assert_eq!(unix_time(), ms(1_731_330_000_123));
        set_unix_millis(-5);
        assert_eq!(unix_time(), Duration::ZERO, "before 1970 counts as 1970");
    }
}
