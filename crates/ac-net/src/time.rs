use core::ops::{Add, AddAssign};
use core::time::Duration;

/// A point on the caller's monotonic clock, as time since an origin the
/// caller picks (typically when the connection attempt started).
///
/// The protocol core never reads a clock: every method that needs the time
/// takes a `NetTime`, so tests can drive it deterministically. Only
/// differences between two `NetTime`s matter.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub struct NetTime(Duration);

impl NetTime {
    /// The origin.
    pub const ZERO: Self = Self(Duration::ZERO);

    /// The time `since_origin` after the origin.
    #[must_use]
    pub const fn from_duration(since_origin: Duration) -> Self {
        Self(since_origin)
    }

    /// Milliseconds after the origin.
    #[must_use]
    pub const fn from_millis(millis: u64) -> Self {
        Self(Duration::from_millis(millis))
    }

    /// Time since the origin.
    #[must_use]
    pub const fn as_duration(self) -> Duration {
        self.0
    }

    /// Time elapsed from `earlier` to `self`, or zero if `earlier` is later.
    #[must_use]
    pub fn since(self, earlier: Self) -> Duration {
        self.0.saturating_sub(earlier.0)
    }
}

impl Add<Duration> for NetTime {
    type Output = Self;
    fn add(self, rhs: Duration) -> Self {
        Self(self.0 + rhs)
    }
}

impl AddAssign<Duration> for NetTime {
    fn add_assign(&mut self, rhs: Duration) {
        self.0 += rhs;
    }
}
